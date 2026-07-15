using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using LinuxCloth.Core;

namespace LinuxCloth.Wsb;

public static class WsbParser
{
    public const int MaximumDocumentCharacters = 1_048_576;

    public static WsbConfiguration Parse(string xml, WsbParseMode mode = WsbParseMode.Normal)
    {
        ArgumentNullException.ThrowIfNull(xml);

        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown WSB parse mode.");
        }

        if (xml.Length > MaximumDocumentCharacters)
        {
            throw new WsbValidationException("The WSB document exceeds the size limit.");
        }

        XDocument document;
        try
        {
            using var textReader = new StringReader(xml);
            using var xmlReader = XmlReader.Create(
                textReader,
                new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaximumDocumentCharacters,
                    MaxCharactersFromEntities = 1,
                    IgnoreComments = false,
                    IgnoreProcessingInstructions = false,
                    IgnoreWhitespace = false,
                });
            document = XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new WsbValidationException("The WSB document is not safe, well-formed XML.", exception);
        }

        ValidateDocumentNodes(document);

        var root = document.Root;
        if (root is null || root.Name != XName.Get("Configuration"))
        {
            throw new WsbValidationException("The WSB root element must be Configuration without a namespace.");
        }

        EnsureNoAttributes(root);
        EnsureOnlyElementsAndWhitespace(root);

        var seenElements = new HashSet<string>(StringComparer.Ordinal);
        var networking = WsbFeatureState.Default;
        var virtualGpu = WsbFeatureState.Default;
        int? memoryInMiB = null;
        var clipboard = WsbFeatureState.Default;
        string? command = null;
        IReadOnlyList<ServiceId>? expressServiceIds = null;
        IReadOnlyList<WsbMappedFolder> mappedFolders = [];

        foreach (var element in root.Elements())
        {
            var canonicalName = element.Name.LocalName switch
            {
                "VGpu" => "vGPU",
                _ => element.Name.LocalName,
            };

            if (element.Name.NamespaceName.Length != 0 || !seenElements.Add(canonicalName))
            {
                throw new WsbValidationException($"The WSB element '{canonicalName}' is namespaced or duplicated.");
            }

            switch (canonicalName)
            {
                case "Networking":
                    networking = ParseFeature(element);
                    break;
                case "vGPU":
                    virtualGpu = ParseFeature(element);
                    break;
                case "MemoryInMB":
                    memoryInMiB = ParseMemory(element);
                    break;
                case "ClipboardRedirection":
                    clipboard = ParseFeature(element);
                    break;
                case "LogonCommand":
                    command = ParseLogonCommand(element);
                    if (ExpressWsbCommand.TryParse(command, out var parsedServiceIds))
                    {
                        expressServiceIds = parsedServiceIds;
                    }
                    else if (mode == WsbParseMode.Normal)
                    {
                        throw new WsbValidationException("Normal mode permits only the official simplified Express logon command.");
                    }

                    break;
                case "MappedFolders":
                    if (mode == WsbParseMode.Normal)
                    {
                        throw new WsbValidationException("Mapped folders are prohibited in normal mode.");
                    }

                    mappedFolders = ParseMappedFolders(element);
                    break;
                default:
                    throw new WsbValidationException($"Unsupported WSB element '{canonicalName}'.");
            }
        }

        try
        {
            return new WsbConfiguration(
                networking,
                virtualGpu,
                memoryInMiB,
                clipboard,
                command,
                mappedFolders,
                expressServiceIds);
        }
        catch (ArgumentException exception)
        {
            throw new WsbValidationException("The WSB document contains an invalid value.", exception);
        }
    }

    private static void ValidateDocumentNodes(XDocument document)
    {
        foreach (var node in document.Nodes())
        {
            if (node is XElement or XComment || node is XText text && string.IsNullOrWhiteSpace(text.Value))
            {
                continue;
            }

            throw new WsbValidationException("The WSB document contains an unsupported document-level node.");
        }
    }

    private static WsbFeatureState ParseFeature(XElement element)
    {
        var value = ReadSimpleValue(element).Trim();
        if (!Enum.TryParse<WsbFeatureState>(value, ignoreCase: true, out var state) || !Enum.IsDefined(state))
        {
            throw new WsbValidationException($"The WSB element '{element.Name.LocalName}' has an invalid feature state.");
        }

        return state;
    }

    private static int ParseMemory(XElement element)
    {
        var value = ReadSimpleValue(element).Trim();
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var memoryInMiB) ||
            memoryInMiB is < 2048 or > 262144)
        {
            throw new WsbValidationException("MemoryInMB must be between 2048 and 262144.");
        }

        return memoryInMiB;
    }

    private static string ParseLogonCommand(XElement element)
    {
        EnsureNoAttributes(element);
        EnsureOnlyElementsAndWhitespace(element);
        var commands = element.Elements().ToArray();
        if (commands.Length != 1 || commands[0].Name != XName.Get("Command"))
        {
            throw new WsbValidationException("LogonCommand must contain exactly one unnamespaced Command element.");
        }

        var command = ReadSimpleValue(commands[0]);
        if (command.Length == 0)
        {
            throw new WsbValidationException("The WSB logon command cannot be empty.");
        }

        return command;
    }

    private static List<WsbMappedFolder> ParseMappedFolders(XElement element)
    {
        EnsureNoAttributes(element);
        EnsureOnlyElementsAndWhitespace(element);

        var folders = new List<WsbMappedFolder>();
        foreach (var folderElement in element.Elements())
        {
            if (folderElement.Name != XName.Get("MappedFolder"))
            {
                throw new WsbValidationException("MappedFolders may contain only unnamespaced MappedFolder elements.");
            }

            EnsureNoAttributes(folderElement);
            EnsureOnlyElementsAndWhitespace(folderElement);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var child in folderElement.Elements())
            {
                if (child.Name.NamespaceName.Length != 0 ||
                    child.Name.LocalName is not ("HostFolder" or "SandboxFolder" or "ReadOnly") ||
                    !values.TryAdd(child.Name.LocalName, ReadSimpleValue(child)))
                {
                    throw new WsbValidationException("A mapped folder contains an unsupported, namespaced, or duplicate element.");
                }
            }

            if (!values.TryGetValue("HostFolder", out var hostFolder))
            {
                throw new WsbValidationException("A mapped folder must specify HostFolder.");
            }

            var readOnly = false;
            if (values.TryGetValue("ReadOnly", out var readOnlyText) &&
                !bool.TryParse(readOnlyText.Trim(), out readOnly))
            {
                throw new WsbValidationException("MappedFolder ReadOnly must be true or false.");
            }

            try
            {
                folders.Add(new WsbMappedFolder(
                    hostFolder,
                    values.GetValueOrDefault("SandboxFolder"),
                    readOnly));
            }
            catch (ArgumentException exception)
            {
                throw new WsbValidationException("A mapped folder contains an invalid path.", exception);
            }
        }

        if (folders.Count == 0)
        {
            throw new WsbValidationException("MappedFolders cannot be empty when present.");
        }

        return folders;
    }

    private static string ReadSimpleValue(XElement element)
    {
        EnsureNoAttributes(element);
        foreach (var node in element.Nodes())
        {
            if (node is not XText)
            {
                throw new WsbValidationException($"The WSB element '{element.Name.LocalName}' must contain text only.");
            }
        }

        return element.Value;
    }

    private static void EnsureNoAttributes(XElement element)
    {
        if (element.HasAttributes)
        {
            throw new WsbValidationException($"The WSB element '{element.Name.LocalName}' must not have attributes.");
        }
    }

    private static void EnsureOnlyElementsAndWhitespace(XContainer container)
    {
        foreach (var node in container.Nodes())
        {
            if (node is XElement or XComment || node is XText text && string.IsNullOrWhiteSpace(text.Value))
            {
                continue;
            }

            throw new WsbValidationException("The WSB document contains unexpected mixed content.");
        }
    }
}
