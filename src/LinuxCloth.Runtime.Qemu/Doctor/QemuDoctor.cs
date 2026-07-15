using System.Runtime.InteropServices;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Runtime.Qemu.Doctor;

public sealed class QemuDoctor
{
    private static readonly (string Name, bool Required)[] Binaries =
    [
        ("qemu-system-x86_64", true),
        ("qemu-img", true),
        ("swtpm", true),
        ("passt", true),
        ("remote-viewer", true),
        ("bwrap", false),
        ("wimlib-imagex", false),
        ("xorriso", false),
    ];

    private readonly IExecutableLocator _locator;
    private readonly IProcessRunner _processRunner;

    public QemuDoctor(IExecutableLocator locator, IProcessRunner processRunner)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<DoctorReport> InspectAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<DoctorCheck>
        {
            CheckPlatform(),
            CheckKvm(),
        };

        foreach (var (name, required) in Binaries)
        {
            checks.Add(await CheckBinaryAsync(name, required, cancellationToken).ConfigureAwait(false));
        }

        checks.Add(CheckFirmware("OVMF_CODE", FindFirmwareCode()));
        checks.Add(CheckFirmware("OVMF_VARS", FindFirmwareVariables()));

        return new DoctorReport(checks);
    }

    private static DoctorCheck CheckPlatform()
    {
        var supported = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
        return new DoctorCheck(
            "platform",
            IsRequired: true,
            IsAvailable: supported,
            supported ? "Linux x86_64" : $"Unsupported {RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}");
    }

    private static DoctorCheck CheckKvm()
    {
        const string path = "/dev/kvm";
        if (!OperatingSystem.IsLinux() || !File.Exists(path))
        {
            return new DoctorCheck("kvm", true, false, "/dev/kvm is missing.", path);
        }

        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return new DoctorCheck("kvm", true, !handle.IsInvalid, "KVM device is readable and writable.", path);
        }
        catch (UnauthorizedAccessException exception)
        {
            return new DoctorCheck("kvm", true, false, exception.Message, path);
        }
        catch (IOException exception)
        {
            return new DoctorCheck("kvm", true, false, exception.Message, path);
        }
    }

    private async Task<DoctorCheck> CheckBinaryAsync(
        string name,
        bool required,
        CancellationToken cancellationToken)
    {
        var path = _locator.Find(name);
        if (path is null)
        {
            return new DoctorCheck(name, required, false, "Executable was not found in PATH.");
        }

        try
        {
            var result = await _processRunner.RunAsync(
                new ProcessSpec(path, ["--version"]),
                cancellationToken).ConfigureAwait(false);
            var detail = FirstLine(result.StandardOutput, result.StandardError);
            return new DoctorCheck(name, required, result.IsSuccess, detail, path);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return new DoctorCheck(name, required, false, exception.Message, path);
        }
    }

    private static DoctorCheck CheckFirmware(string name, string? path) =>
        new(name, true, path is not null, path ?? "Compatible Secure Boot firmware was not found.", path);

    private static string FirstLine(string standardOutput, string standardError)
    {
        var text = string.IsNullOrWhiteSpace(standardOutput) ? standardError : standardOutput;
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Version probe returned no output.";
    }

    private static string? FindFirmwareCode() => FindFirstExisting(
        "/usr/share/edk2/x64/OVMF_CODE.secboot.4m.fd",
        "/usr/share/edk2/x64/OVMF_CODE.secboot.fd",
        "/usr/share/OVMF/OVMF_CODE.secboot.fd",
        "/usr/share/edk2-ovmf/x64/OVMF_CODE.secboot.fd");

    private static string? FindFirmwareVariables() => FindFirstExisting(
        "/usr/share/edk2/x64/OVMF_VARS.4m.fd",
        "/usr/share/edk2/x64/OVMF_VARS.fd",
        "/usr/share/OVMF/OVMF_VARS.fd",
        "/usr/share/edk2-ovmf/x64/OVMF_VARS.fd");

    private static string? FindFirstExisting(params string[] candidates) =>
        candidates.FirstOrDefault(File.Exists);
}

