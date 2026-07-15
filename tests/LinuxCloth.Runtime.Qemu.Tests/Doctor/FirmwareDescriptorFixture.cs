using System.Text.Json;
using LinuxCloth.Runtime.Qemu.Doctor;

namespace LinuxCloth.Runtime.Qemu.Tests.Doctor;

internal sealed class FirmwareDescriptorFixture : IDisposable
{
    private static readonly string[] DefaultFeatures =
    [
        "secure-boot",
        "enrolled-keys",
        "requires-smm",
    ];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"linuxcloth-firmware-test-{Guid.NewGuid():N}");

    public FirmwareDescriptorFixture()
    {
        DescriptorDirectory = Path.Combine(_root, "descriptors");
        ExecutablePath = Path.Combine(_root, "OVMF_CODE.fd");
        NvramTemplatePath = Path.Combine(_root, "OVMF_VARS.fd");
        Directory.CreateDirectory(DescriptorDirectory);
        WriteFirmwareFiles(4096, 4096);
    }

    public string DescriptorDirectory { get; }

    public string ExecutablePath { get; }

    public string NvramTemplatePath { get; }

    public void WriteFirmwareFiles(int executableBytes, int nvramBytes)
    {
        File.WriteAllBytes(ExecutablePath, new byte[executableBytes]);
        File.WriteAllBytes(NvramTemplatePath, new byte[nvramBytes]);
    }

    public string WriteDescriptor(
        string name = "50-linuxcloth.json",
        string? executablePath = null,
        string? nvramPath = null,
        IReadOnlyList<string>? features = null,
        string architecture = "x86_64",
        IReadOnlyList<string>? machines = null)
    {
        var descriptor = new Dictionary<string, object?>
        {
            ["description"] = "linuxcloth test firmware",
            ["interface-types"] = new[] { "uefi" },
            ["mapping"] = new Dictionary<string, object?>
            {
                ["device"] = "flash",
                ["executable"] = new Dictionary<string, object?>
                {
                    ["filename"] = executablePath ?? ExecutablePath,
                    ["format"] = "raw",
                },
                ["nvram-template"] = new Dictionary<string, object?>
                {
                    ["filename"] = nvramPath ?? NvramTemplatePath,
                    ["format"] = "raw",
                },
            },
            ["targets"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["architecture"] = architecture,
                    ["machines"] = machines ?? new[] { "pc-q35-*" },
                },
            },
            ["features"] = features ?? DefaultFeatures,
        };

        var path = Path.Combine(DescriptorDirectory, name);
        File.WriteAllText(path, JsonSerializer.Serialize(descriptor));
        return path;
    }

    public string WriteRawDescriptor(string name, ReadOnlySpan<byte> bytes)
    {
        var path = Path.Combine(DescriptorDirectory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
