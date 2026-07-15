using System.Xml;
using LinuxCloth.Core;

namespace LinuxCloth.Catalog;

public sealed class CatalogParser
{
    public const int MaximumDocumentBytes = 16 * 1024 * 1024;
    private readonly int _maximumDocumentBytes = MaximumDocumentBytes;
    private readonly CatalogDuplicateIdPolicy _duplicateIdPolicy;

    public CatalogParser(CatalogDuplicateIdPolicy duplicateIdPolicy = CatalogDuplicateIdPolicy.Reject)
    {
        _duplicateIdPolicy = duplicateIdPolicy;
    }

    public CatalogDocument Parse(ReadOnlyMemory<byte> document)
    {
        using var stream = new MemoryStream(document.ToArray(), writable: false);
        return Parse(stream);
    }

    public CatalogDocument Parse(Stream document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.CanSeek && document.Length - document.Position > _maximumDocumentBytes)
        {
            throw new CatalogValidationException(
                $"The catalog exceeds the {_maximumDocumentBytes}-byte limit.");
        }

        var settings = new XmlReaderSettings
        {
            Async = false,
            CloseInput = false,
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = _maximumDocumentBytes,
            XmlResolver = null,
        };

        try
        {
            using var bounded = new BoundedReadStream(document, _maximumDocumentBytes);
            using var reader = XmlReader.Create(bounded, settings);
            return ParseDocument(reader);
        }
        catch (CatalogValidationException)
        {
            throw;
        }
        catch (XmlException exception)
        {
            throw new CatalogValidationException("The catalog XML is not safe or well formed.", exception);
        }
    }

    private CatalogDocument ParseDocument(XmlReader reader)
    {
        if (reader.MoveToContent() != XmlNodeType.Element ||
            !string.Equals(reader.LocalName, "TableClothCatalog", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(reader.NamespaceURI))
        {
            throw new CatalogValidationException("The root element must be an unqualified TableClothCatalog element.");
        }

        var fallbackLanguage = OptionalAttribute(reader, "Fallback");
        var services = new List<CatalogService>();
        var diagnostics = new List<CatalogDiagnostic>();
        var serviceIds = new HashSet<string>(StringComparer.Ordinal);
        var rootDepth = reader.Depth;

        if (reader.IsEmptyElement)
        {
            return new CatalogDocument(fallbackLanguage, services.AsReadOnly(), diagnostics.AsReadOnly());
        }

        ReadNext(reader, "TableClothCatalog");
        while (!IsEndElement(reader, rootDepth, "TableClothCatalog"))
        {
            if (IsDirectElement(reader, rootDepth, "InternetServices"))
            {
                ParseInternetServices(reader, services, serviceIds, diagnostics);
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.Depth == rootDepth + 1)
            {
                reader.Skip();
            }
            else
            {
                ReadNext(reader, "TableClothCatalog");
            }
        }

        return new CatalogDocument(fallbackLanguage, services.AsReadOnly(), diagnostics.AsReadOnly());
    }

    private void ParseInternetServices(
        XmlReader reader,
        List<CatalogService> services,
        HashSet<string> serviceIds,
        List<CatalogDiagnostic> diagnostics)
    {
        var containerDepth = reader.Depth;
        if (reader.IsEmptyElement)
        {
            ReadNext(reader, "InternetServices");
            return;
        }

        ReadNext(reader, "InternetServices");
        while (!IsEndElement(reader, containerDepth, "InternetServices"))
        {
            if (IsDirectElement(reader, containerDepth, "Service"))
            {
                var service = ParseService(reader);
                if (!serviceIds.Add(service.Id.Value))
                {
                    if (_duplicateIdPolicy == CatalogDuplicateIdPolicy.Reject)
                    {
                        throw new CatalogValidationException(
                            $"The service identifier '{service.Id}' is duplicated.");
                    }

                    diagnostics.Add(new CatalogDiagnostic(
                        CatalogDiagnosticCode.DuplicateServiceId,
                        $"Ignored the later duplicate service identifier '{service.Id}'.",
                        service.Id));
                    continue;
                }

                services.Add(service);
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.Depth == containerDepth + 1)
            {
                reader.Skip();
            }
            else
            {
                ReadNext(reader, "InternetServices");
            }
        }

        ReadNext(reader, "InternetServices");
    }

    private static CatalogService ParseService(XmlReader reader)
    {
        var idText = RequiredAttribute(reader, "Id", "Service");
        if (!ServiceId.TryCreate(idText, out var id))
        {
            throw new CatalogValidationException($"The service identifier '{idText}' is invalid.");
        }

        var displayName = RequiredAttribute(reader, "DisplayName", idText);
        var englishDisplayName = OptionalAttribute(reader, "en-US-DisplayName");
        var categoryText = RequiredAttribute(reader, "Category", idText);
        if (!Enum.TryParse<CatalogCategory>(categoryText, ignoreCase: false, out var category) ||
            !Enum.IsDefined(category))
        {
            throw new CatalogValidationException(
                $"Service '{idText}' has an unknown category '{categoryText}'.");
        }

        var url = ParseWebUri(RequiredAttribute(reader, "Url", idText), $"service '{idText}'");
        string? compatNotes = null;
        string? englishCompatNotes = null;
        string? customBootstrap = null;
        var keywords = new List<string>();
        var packages = new List<CatalogPackage>();
        var extensions = new List<CatalogEdgeExtension>();
        var serviceDepth = reader.Depth;

        if (reader.IsEmptyElement)
        {
            ReadNext(reader, idText);
        }
        else
        {
            ReadNext(reader, idText);
            while (!IsEndElement(reader, serviceDepth, "Service"))
            {
                if (IsDirectElement(reader, serviceDepth, "CompatNotes"))
                {
                    compatNotes = reader.ReadElementContentAsString();
                }
                else if (IsDirectElement(reader, serviceDepth, "en-US-CompatNotes"))
                {
                    englishCompatNotes = reader.ReadElementContentAsString();
                }
                else if (IsDirectElement(reader, serviceDepth, "SearchKeywords"))
                {
                    AddKeywords(keywords, reader.ReadElementContentAsString());
                }
                else if (IsDirectElement(reader, serviceDepth, "CustomBootstrap"))
                {
                    customBootstrap = reader.ReadElementContentAsString();
                }
                else if (IsDirectElement(reader, serviceDepth, "Packages"))
                {
                    ParsePackages(reader, id, packages);
                }
                else if (IsDirectElement(reader, serviceDepth, "EdgeExtensions"))
                {
                    ParseEdgeExtensions(reader, id, extensions);
                }
                else if (reader.NodeType == XmlNodeType.Element && reader.Depth == serviceDepth + 1)
                {
                    reader.Skip();
                }
                else
                {
                    ReadNext(reader, idText);
                }
            }

            ReadNext(reader, idText);
        }

        return new CatalogService(
            id,
            displayName,
            englishDisplayName,
            category,
            url,
            compatNotes,
            englishCompatNotes,
            keywords.AsReadOnly(),
            packages.AsReadOnly(),
            extensions.AsReadOnly(),
            customBootstrap);
    }

    private static void ParsePackages(
        XmlReader reader,
        ServiceId serviceId,
        List<CatalogPackage> packages)
    {
        var containerDepth = reader.Depth;
        if (reader.IsEmptyElement)
        {
            ReadNext(reader, "Packages");
            return;
        }

        ReadNext(reader, "Packages");
        while (!IsEndElement(reader, containerDepth, "Packages"))
        {
            if (IsDirectElement(reader, containerDepth, "Package"))
            {
                var name = RequiredAttribute(reader, "Name", $"package in '{serviceId}'");
                var url = ParseWebUri(
                    RequiredAttribute(reader, "Url", $"package '{name}'"),
                    $"package '{name}' in service '{serviceId}'");
                packages.Add(new CatalogPackage(name, url, OptionalAttribute(reader, "Arguments")));
                reader.Skip();
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.Depth == containerDepth + 1)
            {
                reader.Skip();
            }
            else
            {
                ReadNext(reader, "Packages");
            }
        }

        ReadNext(reader, "Packages");
    }

    private static void ParseEdgeExtensions(
        XmlReader reader,
        ServiceId serviceId,
        List<CatalogEdgeExtension> extensions)
    {
        var containerDepth = reader.Depth;
        if (reader.IsEmptyElement)
        {
            ReadNext(reader, "EdgeExtensions");
            return;
        }

        ReadNext(reader, "EdgeExtensions");
        while (!IsEndElement(reader, containerDepth, "EdgeExtensions"))
        {
            if (IsDirectElement(reader, containerDepth, "EdgeExtension"))
            {
                var name = RequiredAttribute(reader, "Name", $"extension in '{serviceId}'");
                var url = ParseWebUri(
                    RequiredAttribute(reader, "CrxUrl", $"extension '{name}'"),
                    $"extension '{name}' in service '{serviceId}'");
                extensions.Add(
                    new CatalogEdgeExtension(name, url, OptionalAttribute(reader, "ExtensionId")));
                reader.Skip();
            }
            else if (reader.NodeType == XmlNodeType.Element && reader.Depth == containerDepth + 1)
            {
                reader.Skip();
            }
            else
            {
                ReadNext(reader, "EdgeExtensions");
            }
        }

        ReadNext(reader, "EdgeExtensions");
    }

    private static void AddKeywords(List<string> keywords, string value)
    {
        foreach (var keyword in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!keywords.Contains(keyword, StringComparer.Ordinal))
            {
                keywords.Add(keyword);
            }
        }
    }

    private static string RequiredAttribute(XmlReader reader, string name, string owner)
    {
        var value = reader.GetAttribute(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CatalogValidationException($"{owner} is missing the required {name} attribute.");
        }

        return value;
    }

    private static string? OptionalAttribute(XmlReader reader, string name) => reader.GetAttribute(name);

    private static Uri ParseWebUri(string value, string owner)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
        {
            throw new CatalogValidationException(
                $"The URL for {owner} must be an absolute HTTP or HTTPS URL.");
        }

        return uri;
    }

    private static bool IsDirectElement(XmlReader reader, int parentDepth, string localName) =>
        reader.NodeType == XmlNodeType.Element &&
        reader.Depth == parentDepth + 1 &&
        string.Equals(reader.LocalName, localName, StringComparison.Ordinal);

    private static bool IsEndElement(XmlReader reader, int depth, string localName) =>
        reader.NodeType == XmlNodeType.EndElement &&
        reader.Depth == depth &&
        string.Equals(reader.LocalName, localName, StringComparison.Ordinal);

    private static void ReadNext(XmlReader reader, string element)
    {
        if (!reader.Read())
        {
            throw new CatalogValidationException($"The {element} element ended unexpectedly.");
        }
    }
}
