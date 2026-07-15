using System.Runtime.InteropServices;
using System.Text;
using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Qemu;

namespace LinuxCloth.Runtime.Qemu.Doctor;

public sealed class QemuDoctor
{
    private static readonly (string Code, bool RequiredForLaunch)[] Executables =
    [
        (QemuDoctorCheckCodes.QemuSystem, true),
        (QemuDoctorCheckCodes.QemuImg, true),
        (QemuDoctorCheckCodes.Swtpm, true),
        (QemuDoctorCheckCodes.RemoteViewer, true),
        (QemuDoctorCheckCodes.Passt, true),
        (QemuDoctorCheckCodes.Bubblewrap, true),
        (QemuDoctorCheckCodes.WimlibImagex, false),
        (QemuDoctorCheckCodes.Xorriso, false),
    ];

    private readonly IExecutableLocator _locator;
    private readonly QemuDoctorOptions _options;
    private readonly IQemuDoctorHostProbe _host;
    private readonly FirmwareDescriptorResolver _firmwareResolver;

    public QemuDoctor(IExecutableLocator locator, IProcessRunner processRunner)
        : this(locator, new QemuDoctorOptions(), new SystemQemuDoctorHostProbe())
    {
        // Retained for source compatibility. Doctor checks no longer execute binaries or infer
        // capabilities from version text, so the process runner is intentionally unused.
        ArgumentNullException.ThrowIfNull(processRunner);
    }

    public QemuDoctor(
        IExecutableLocator locator,
        QemuDoctorOptions options,
        IQemuDoctorHostProbe host)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _host = host ?? throw new ArgumentNullException(nameof(host));

        ValidateOptions(options);
        _firmwareResolver = new FirmwareDescriptorResolver(options.FirmwareDescriptorDirectory);
    }

    public async Task<DoctorReport> InspectAsync(CancellationToken cancellationToken = default) =>
        (await InspectDetailedAsync(cancellationToken).ConfigureAwait(false)).Report;

    public Task<QemuDoctorResult> InspectDetailedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var checks = new List<DoctorCheck>
        {
            CheckPlatform(),
            CheckKvm(),
        };

        foreach (var (code, requiredForLaunch) in Executables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checks.Add(CheckExecutable(code, requiredForLaunch));
        }

        var firmwareResolution = _firmwareResolver.Resolve();
        checks.Add(CheckFirmware(firmwareResolution));

        var runtimeDirectory = CheckRuntimeDirectory();
        checks.Add(runtimeDirectory.Check);

        var report = new DoctorReport(checks.AsReadOnly());
        var launchPrerequisites = report.CanLaunch &&
                                  firmwareResolution.Pair is not null &&
                                  runtimeDirectory.ResolvedPath is not null
            ? BuildLaunchPrerequisites(
                report,
                firmwareResolution.Pair,
                runtimeDirectory.ResolvedPath,
                requireNetwork: true)
            : null;
        var offlineLaunchPrerequisites = CanLaunchOffline(report) &&
                                         firmwareResolution.Pair is not null &&
                                         runtimeDirectory.ResolvedPath is not null
            ? BuildLaunchPrerequisites(
                report,
                firmwareResolution.Pair,
                runtimeDirectory.ResolvedPath,
                requireNetwork: false)
            : null;

        var imageBuildPrerequisites = BuildImagePrerequisites(report);
        return Task.FromResult(
            new QemuDoctorResult(
                report,
                launchPrerequisites,
                imageBuildPrerequisites,
                offlineLaunchPrerequisites));
    }

    private DoctorCheck CheckPlatform()
    {
        var supported = _host.IsLinux && _host.ProcessArchitecture == Architecture.X64;
        return new DoctorCheck(
            QemuDoctorCheckCodes.Platform,
            IsRequired: true,
            IsAvailable: supported,
            supported
                ? "Host platform is Linux x86_64."
                : $"Unsupported host '{_host.PlatformDescription}' architecture " +
                  $"'{_host.ProcessArchitecture}'. linuxcloth requires Linux x86_64.");
    }

    private DoctorCheck CheckKvm()
    {
        var result = _host.ProbeReadWriteDevice(_options.KvmDevicePath);
        return new DoctorCheck(
            QemuDoctorCheckCodes.Kvm,
            IsRequired: true,
            result.IsAvailable,
            result.Detail,
            _options.KvmDevicePath);
    }

    private DoctorCheck CheckExecutable(string code, bool requiredForLaunch)
    {
        var path = _locator.Find(code);
        if (path is null)
        {
            var purpose = requiredForLaunch ? "launching disposable VMs" : "building Windows images";
            return new DoctorCheck(
                code,
                requiredForLaunch,
                IsAvailable: false,
                $"Executable '{code}' was not found in PATH. Install the distribution package that provides it before {purpose}.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return new DoctorCheck(
                code,
                requiredForLaunch,
                IsAvailable: false,
                $"Executable locator returned non-absolute path '{path}'. Configure an absolute executable path.");
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return new DoctorCheck(code, requiredForLaunch, false, $"Executable path is invalid: {exception.Message}");
        }

        if (!File.Exists(normalizedPath))
        {
            return new DoctorCheck(
                code,
                requiredForLaunch,
                IsAvailable: false,
                $"Resolved executable '{normalizedPath}' does not exist.",
                normalizedPath);
        }

        if (_host.IsLinux && OperatingSystem.IsLinux())
        {
            try
            {
                const UnixFileMode executableBits =
                    UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                if ((File.GetUnixFileMode(normalizedPath) & executableBits) == 0)
                {
                    return new DoctorCheck(
                        code,
                        requiredForLaunch,
                        IsAvailable: false,
                        $"Resolved file '{normalizedPath}' has no executable permission bit.",
                        normalizedPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new DoctorCheck(
                    code,
                    requiredForLaunch,
                    IsAvailable: false,
                    $"Resolved executable '{normalizedPath}' could not be inspected: {exception.Message}",
                    normalizedPath);
            }
        }

        return new DoctorCheck(
            code,
            requiredForLaunch,
            IsAvailable: true,
            $"Executable '{code}' resolved to an absolute executable path.",
            normalizedPath);
    }

    private static DoctorCheck CheckFirmware(FirmwareResolution resolution)
    {
        if (resolution.Pair is not null)
        {
            return new DoctorCheck(
                QemuDoctorCheckCodes.Firmware,
                IsRequired: true,
                IsAvailable: true,
                "Resolved Q35 x86_64 UEFI firmware with Secure Boot, enrolled keys, and SMM support.",
                resolution.Pair.DescriptorPath);
        }

        var diagnostics = resolution.Diagnostics.Count == 0
            ? "No firmware diagnostics were returned."
            : string.Join(
                " ",
                resolution.Diagnostics.Select(static diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"));
        return new DoctorCheck(
            QemuDoctorCheckCodes.Firmware,
            IsRequired: true,
            IsAvailable: false,
            $"Compatible Secure Boot firmware could not be resolved. {diagnostics}");
    }

    private RuntimeDirectoryCheck CheckRuntimeDirectory()
    {
        if (string.IsNullOrWhiteSpace(_options.XdgRuntimeDirectory))
        {
            return new RuntimeDirectoryCheck(
                new DoctorCheck(
                    QemuDoctorCheckCodes.RuntimeDirectory,
                    IsRequired: true,
                    IsAvailable: false,
                    "XDG_RUNTIME_DIR is not set. Start linuxcloth from a graphical or login user session."),
                null);
        }

        if (!Path.IsPathFullyQualified(_options.XdgRuntimeDirectory))
        {
            return new RuntimeDirectoryCheck(
                new DoctorCheck(
                    QemuDoctorCheckCodes.RuntimeDirectory,
                    IsRequired: true,
                    IsAvailable: false,
                    $"XDG_RUNTIME_DIR '{_options.XdgRuntimeDirectory}' must be an absolute path."),
                null);
        }

        string xdgRuntimeDirectory;
        try
        {
            xdgRuntimeDirectory = Path.GetFullPath(_options.XdgRuntimeDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return new RuntimeDirectoryCheck(
                new DoctorCheck(
                    QemuDoctorCheckCodes.RuntimeDirectory,
                    IsRequired: true,
                    IsAvailable: false,
                    $"XDG_RUNTIME_DIR is invalid: {exception.Message}"),
                null);
        }

        var linuxClothRuntimeDirectory = Path.Combine(xdgRuntimeDirectory, "linuxcloth");
        var longestSocketPath = Path.Combine(
            linuxClothRuntimeDirectory,
            "sessions",
            new string('f', 32),
            "spice.sock");
        var socketPathBytes = Encoding.UTF8.GetByteCount(longestSocketPath);
        if (socketPathBytes > _options.MaximumUnixSocketPathBytes)
        {
            return new RuntimeDirectoryCheck(
                new DoctorCheck(
                    QemuDoctorCheckCodes.RuntimeDirectory,
                    IsRequired: true,
                    IsAvailable: false,
                    $"Session socket path would be {socketPathBytes} UTF-8 bytes, exceeding the " +
                    $"{_options.MaximumUnixSocketPathBytes}-byte safety limit. Use a shorter XDG_RUNTIME_DIR.",
                    xdgRuntimeDirectory),
                null);
        }

        var probe = _host.ProbeUnixSocketDirectory(xdgRuntimeDirectory);
        return new RuntimeDirectoryCheck(
            new DoctorCheck(
                QemuDoctorCheckCodes.RuntimeDirectory,
                IsRequired: true,
                probe.IsAvailable,
                probe.Detail,
                probe.IsAvailable ? linuxClothRuntimeDirectory : xdgRuntimeDirectory),
            probe.IsAvailable ? linuxClothRuntimeDirectory : null);
    }

    private static QemuLaunchPrerequisites BuildLaunchPrerequisites(
        DoctorReport report,
        FirmwarePair firmware,
        string runtimeDirectory,
        bool requireNetwork) =>
        new(
            new QemuToolchain(
                GetRequiredPath(report, QemuDoctorCheckCodes.QemuSystem),
                GetRequiredPath(report, QemuDoctorCheckCodes.QemuImg),
                GetRequiredPath(report, QemuDoctorCheckCodes.Swtpm),
                requireNetwork
                    ? GetRequiredPath(report, QemuDoctorCheckCodes.Passt)
                    : report.FindPath(QemuDoctorCheckCodes.Passt),
                GetRequiredPath(report, QemuDoctorCheckCodes.RemoteViewer)),
            firmware,
            GetRequiredPath(report, QemuDoctorCheckCodes.Bubblewrap),
            runtimeDirectory);

    private static bool CanLaunchOffline(DoctorReport report) =>
        report.Checks.All(check =>
            !check.IsRequired ||
            check.IsAvailable ||
            string.Equals(check.Name, QemuDoctorCheckCodes.Passt, StringComparison.Ordinal));

    private static ImageBuildPrerequisites? BuildImagePrerequisites(DoctorReport report)
    {
        var wimlib = report.FindPath(QemuDoctorCheckCodes.WimlibImagex);
        var xorriso = report.FindPath(QemuDoctorCheckCodes.Xorriso);
        return wimlib is not null && xorriso is not null
            ? new ImageBuildPrerequisites(wimlib, xorriso)
            : null;
    }

    private static string GetRequiredPath(DoctorReport report, string code) =>
        report.FindPath(code) ?? throw new InvalidOperationException($"Required doctor check '{code}' has no resolved path.");

    private static void ValidateOptions(QemuDoctorOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.KvmDevicePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FirmwareDescriptorDirectory);

        if (!Path.IsPathFullyQualified(options.KvmDevicePath))
        {
            throw new ArgumentException("The KVM device path must be absolute.", nameof(options));
        }

        if (!Path.IsPathFullyQualified(options.FirmwareDescriptorDirectory))
        {
            throw new ArgumentException("The firmware descriptor directory must be absolute.", nameof(options));
        }

        if (options.MaximumUnixSocketPathBytes is <= 0 or > QemuDoctorOptions.MaximumSessionSocketPathBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                $"The Unix socket path limit must be between 1 and {QemuDoctorOptions.MaximumSessionSocketPathBytes} bytes.");
        }
    }

    private sealed record RuntimeDirectoryCheck(DoctorCheck Check, string? ResolvedPath);
}
