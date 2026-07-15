using System.Text;
using System.Xml;

namespace LinuxCloth.Wsb;

public static class WsbSerializer
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Serialize(WsbConfiguration configuration) =>
        Utf8WithoutBom.GetString(SerializeToUtf8Bytes(configuration));

    public static byte[] SerializeToUtf8Bytes(WsbConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(
                   output,
                   new XmlWriterSettings
                   {
                       Encoding = Utf8WithoutBom,
                       Indent = true,
                       IndentChars = "  ",
                       NewLineChars = "\n",
                       NewLineHandling = NewLineHandling.None,
                       OmitXmlDeclaration = false,
                       CloseOutput = false,
                   }))
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("Configuration");

            WriteFeature(writer, "Networking", configuration.Networking);
            WriteFeature(writer, "vGPU", configuration.VirtualGpu);

            if (configuration.MemoryInMiB is int memoryInMiB)
            {
                writer.WriteElementString("MemoryInMB", memoryInMiB.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            WriteFeature(writer, "ClipboardRedirection", configuration.ClipboardRedirection);

            if (configuration.MappedFolders.Count > 0)
            {
                writer.WriteStartElement("MappedFolders");
                foreach (var folder in configuration.MappedFolders)
                {
                    writer.WriteStartElement("MappedFolder");
                    writer.WriteElementString("HostFolder", folder.HostFolder);
                    if (folder.SandboxFolder is not null)
                    {
                        writer.WriteElementString("SandboxFolder", folder.SandboxFolder);
                    }

                    writer.WriteElementString("ReadOnly", XmlConvert.ToString(folder.ReadOnly));
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
            }

            if (configuration.LogonCommand is not null)
            {
                writer.WriteStartElement("LogonCommand");
                writer.WriteElementString("Command", configuration.LogonCommand);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return output.ToArray();
    }

    private static void WriteFeature(XmlWriter writer, string elementName, WsbFeatureState value)
    {
        if (value != WsbFeatureState.Default)
        {
            writer.WriteElementString(elementName, value.ToString());
        }
    }
}
