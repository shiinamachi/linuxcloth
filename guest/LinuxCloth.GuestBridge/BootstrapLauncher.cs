using System.ComponentModel;
using System.Diagnostics;
using LinuxCloth.Core;

namespace LinuxCloth.GuestBridge;

internal interface IBootstrapLauncher
{
    Task<int> LaunchAsync(
        IReadOnlyList<ServiceId> serviceIds,
        CancellationToken cancellationToken);
}

internal interface IProcessRunner
{
    Task<int> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken);
}

internal sealed class PowerShellBootstrapLauncher : IBootstrapLauncher
{
    internal const string SiteIdsEnvironmentVariable = "TABLECLOTH_SITE_IDS";
    internal const string ScriptUrlEnvironmentVariable = "LINUXCLOTH_OFFICIAL_SCRIPT_URL";
    internal const string OfficialScriptUrl =
        "https://github.com/yourtablecloth/TableCloth/releases/latest/download/tablecloth-prepare.ps1";

    internal const string FixedBootstrapCommand =
        "$ErrorActionPreference = 'Stop'; " +
        "[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072; " +
        "Invoke-Expression ((New-Object Net.WebClient).DownloadString($env:LINUXCLOTH_OFFICIAL_SCRIPT_URL))";

    private readonly IProcessRunner _processRunner;

    public PowerShellBootstrapLauncher(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public Task<int> LaunchAsync(
        IReadOnlyList<ServiceId> serviceIds,
        CancellationToken cancellationToken)
    {
        var validatedServiceIds = ValidateServiceIds(serviceIds);
        var startInfo = CreateStartInfo(validatedServiceIds);
        return _processRunner.RunAsync(startInfo, cancellationToken);
    }

    private static ProcessStartInfo CreateStartInfo(IReadOnlyList<ServiceId> serviceIds)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = GetWindowsPowerShellPath(),
            UseShellExecute = false,
            CreateNoWindow = false,
            ErrorDialog = false,
        };

        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(FixedBootstrapCommand);
        startInfo.Environment[SiteIdsEnvironmentVariable] =
            string.Join(' ', serviceIds.Select(serviceId => serviceId.Value));
        startInfo.Environment[ScriptUrlEnvironmentVariable] = OfficialScriptUrl;
        return startInfo;
    }

    private static ServiceId[] ValidateServiceIds(IReadOnlyList<ServiceId> serviceIds)
    {
        ArgumentNullException.ThrowIfNull(serviceIds);
        if (serviceIds.Count is 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(
                nameof(serviceIds),
                "Between 1 and 32 service identifiers are required.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var copy = new ServiceId[serviceIds.Count];
        for (var index = 0; index < serviceIds.Count; index++)
        {
            var serviceId = serviceIds[index];
            if (!ServiceId.TryCreate(serviceId.Value, out var validated) || !seen.Add(validated.Value))
            {
                throw new ArgumentException(
                    "Service identifiers must be valid and unique.",
                    nameof(serviceIds));
            }

            copy[index] = validated;
        }

        return copy;
    }

    private static string GetWindowsPowerShellPath()
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return string.IsNullOrEmpty(systemDirectory)
            ? "powershell.exe"
            : Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    }
}

internal sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Windows PowerShell did not start.");
        }

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            throw;
        }

        return process.ExitCode;
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited while cancellation was being handled.
        }
        catch (Win32Exception)
        {
            // Best-effort cancellation cannot safely expose process details.
        }
        catch (NotSupportedException)
        {
            // Best-effort cancellation cannot safely expose process details.
        }
    }
}
