using System.Diagnostics;

namespace LinuxCloth.GuestBridge;

internal interface IShutdownRequester
{
    Task<int> RequestShutdownAsync(CancellationToken cancellationToken);
}

internal sealed class NullShutdownRequester : IShutdownRequester
{
    public static NullShutdownRequester Instance { get; } = new();

    private NullShutdownRequester()
    {
    }

    public Task<int> RequestShutdownAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(1);
    }
}

internal sealed class WindowsShutdownRequester : IShutdownRequester
{
    private readonly IProcessRunner _processRunner;

    public WindowsShutdownRequester(IProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public Task<int> RequestShutdownAsync(CancellationToken cancellationToken)
    {
        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrEmpty(systemDirectory)
                ? "shutdown.exe"
                : Path.Combine(systemDirectory, "shutdown.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            ErrorDialog = false,
        };
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/t");
        startInfo.ArgumentList.Add("0");
        return _processRunner.RunAsync(startInfo, cancellationToken);
    }
}
