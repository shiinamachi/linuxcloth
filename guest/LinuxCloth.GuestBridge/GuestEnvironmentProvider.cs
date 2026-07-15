using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace LinuxCloth.GuestBridge;

internal sealed record GuestEnvironmentProvenance(
    string GuestBridgeVersion,
    string WindowsArchitecture,
    int WindowsBuild,
    string WindowsEditionId,
    string WindowsDisplayVersion);

internal interface IGuestEnvironmentProvider
{
    GuestEnvironmentProvenance GetProvenance();
}

internal sealed class SystemGuestEnvironmentProvider : IGuestEnvironmentProvider
{
    private const string CurrentVersionKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public GuestEnvironmentProvenance GetProvenance()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows provenance is available only inside Windows.");
        }

        if (RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            throw new InvalidDataException("GuestBridge provisioning requires Windows x64.");
        }

        var assembly = Assembly.GetEntryAssembly() ?? typeof(SystemGuestEnvironmentProvider).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var guestBridgeVersion = !string.IsNullOrWhiteSpace(informationalVersion)
            ? informationalVersion
            : assembly.GetName().Version?.ToString();

        var windowsBuild = Environment.OSVersion.Version.Build;
        using var localMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var currentVersion = localMachine.OpenSubKey(CurrentVersionKeyPath, writable: false)
            ?? throw new InvalidDataException("The Windows current-version registry key is unavailable.");
        var editionId = ReadFirstNonEmptyString(
            currentVersion,
            "EditionID",
            "CompositionEditionID",
            "ProductName");
        var displayVersion = ReadFirstNonEmptyString(
                                 currentVersion,
                                 "DisplayVersion",
                                 "ReleaseId",
                                 "CurrentBuildNumber")
                             ?? windowsBuild.ToString(CultureInfo.InvariantCulture);

        return new GuestEnvironmentProvenance(
            guestBridgeVersion ?? string.Empty,
            "X64",
            windowsBuild,
            editionId ?? string.Empty,
            displayVersion);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadFirstNonEmptyString(RegistryKey key, params string[] names)
    {
        foreach (var name in names)
        {
            if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string value &&
                !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
