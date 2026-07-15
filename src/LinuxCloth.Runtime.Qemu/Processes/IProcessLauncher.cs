namespace LinuxCloth.Runtime.Qemu.Processes;

public interface IProcessLauncher
{
    Task<IManagedProcess> StartAsync(ProcessSpec spec, CancellationToken cancellationToken = default);
}

public interface IManagedProcess : IAsyncDisposable
{
    int Id { get; }

    ProcessIdentity Identity { get; }

    bool HasExited { get; }

    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);

    Task TerminateAsync(CancellationToken cancellationToken = default);

    Task KillAsync(CancellationToken cancellationToken = default);
}

public sealed record ProcessIdentity(
    int ProcessId,
    string BootId,
    long StartTicks,
    string ExecutablePath);

