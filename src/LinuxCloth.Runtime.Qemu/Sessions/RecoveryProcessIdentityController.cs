using System.Diagnostics;
using System.Runtime.InteropServices;
using LinuxCloth.Runtime.Qemu.Processes;
using Microsoft.Win32.SafeHandles;

namespace LinuxCloth.Runtime.Qemu.Sessions;

public enum RecoveryProcessStatus
{
    Running,
    Stopped,
    IdentityMismatch,
}

public enum RecoverySignalResult
{
    Sent,
    Stopped,
    IdentityMismatch,
}

public interface IProcessIdentityController
{
    Task<RecoveryProcessStatus> InspectAsync(
        ProcessIdentity expectedIdentity,
        CancellationToken cancellationToken = default);

    Task<RecoverySignalResult> SendTerminateAsync(
        ProcessIdentity expectedIdentity,
        CancellationToken cancellationToken = default);

    Task<RecoverySignalResult> SendKillAsync(
        ProcessIdentity expectedIdentity,
        CancellationToken cancellationToken = default);

    Task<RecoveryProcessStatus> WaitForExitAsync(
        ProcessIdentity expectedIdentity,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Controls Linux processes through pidfds. Each signal opens a new pidfd and
/// revalidates PID, boot ID, start ticks, and executable path before signaling.
/// </summary>
public sealed partial class LinuxProcessIdentityController : IProcessIdentityController
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    public Task<RecoveryProcessStatus> InspectAsync(
        ProcessIdentity expectedIdentity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        cancellationToken.ThrowIfCancellationRequested();

        using var verified = OpenVerified(expectedIdentity);
        return Task.FromResult(verified.Status);
    }

    public Task<RecoverySignalResult> SendTerminateAsync(
        ProcessIdentity expectedIdentity,
        CancellationToken cancellationToken = default) =>
        SendSignalAsync(expectedIdentity, NativeMethods.SigTerm, cancellationToken);

    public Task<RecoverySignalResult> SendKillAsync(
        ProcessIdentity expectedIdentity,
        CancellationToken cancellationToken = default) =>
        SendSignalAsync(expectedIdentity, NativeMethods.SigKill, cancellationToken);

    public async Task<RecoveryProcessStatus> WaitForExitAsync(
        ProcessIdentity expectedIdentity,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var status = await InspectAsync(expectedIdentity, cancellationToken).ConfigureAwait(false);
            if (status != RecoveryProcessStatus.Running || stopwatch.Elapsed >= timeout)
            {
                return status;
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return RecoveryProcessStatus.Running;
            }

            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<RecoverySignalResult> SendSignalAsync(
        ProcessIdentity expectedIdentity,
        int signal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        cancellationToken.ThrowIfCancellationRequested();

        using var verified = OpenVerified(expectedIdentity);
        if (verified.Status == RecoveryProcessStatus.Stopped)
        {
            return Task.FromResult(RecoverySignalResult.Stopped);
        }

        if (verified.Status == RecoveryProcessStatus.IdentityMismatch)
        {
            return Task.FromResult(RecoverySignalResult.IdentityMismatch);
        }

        if (NativeMethods.PidfdSendSignal(
                verified.Handle!.DangerousGetHandle().ToInt32(),
                signal,
                0,
                0) == 0)
        {
            return Task.FromResult(RecoverySignalResult.Sent);
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == NativeMethods.NoSuchProcess)
        {
            return Task.FromResult(RecoverySignalResult.Stopped);
        }

        throw new IOException(
            $"Failed to send signal {signal} to verified process {expectedIdentity.ProcessId}; errno={error}.");
    }

    private static VerifiedProcess OpenVerified(ProcessIdentity expectedIdentity)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Crash recovery process control requires Linux pidfds.");
        }

        int descriptor;
        try
        {
            descriptor = NativeMethods.PidfdOpen(expectedIdentity.ProcessId, 0);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new PlatformNotSupportedException("The host C library does not expose pidfd_open.", exception);
        }

        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == NativeMethods.NoSuchProcess)
            {
                return new VerifiedProcess(RecoveryProcessStatus.Stopped, null);
            }

            if (error == NativeMethods.FunctionNotImplemented)
            {
                throw new PlatformNotSupportedException("The host kernel does not support pidfd_open.");
            }

            throw new IOException($"Could not open pidfd for process {expectedIdentity.ProcessId}; errno={error}.");
        }

        var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        try
        {
            ProcessIdentity actualIdentity;
            try
            {
                using var process = Process.GetProcessById(expectedIdentity.ProcessId);
                actualIdentity = LinuxProcessIdentity.Read(process);
            }
            catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or DirectoryNotFoundException)
            {
                handle.Dispose();
                return new VerifiedProcess(RecoveryProcessStatus.Stopped, null);
            }

            if (actualIdentity != expectedIdentity)
            {
                handle.Dispose();
                return new VerifiedProcess(RecoveryProcessStatus.IdentityMismatch, null);
            }

            return new VerifiedProcess(RecoveryProcessStatus.Running, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private sealed class VerifiedProcess(RecoveryProcessStatus status, SafeFileHandle? handle) : IDisposable
    {
        public RecoveryProcessStatus Status { get; } = status;

        public SafeFileHandle? Handle { get; } = handle;

        public void Dispose() => Handle?.Dispose();
    }

    private static partial class NativeMethods
    {
        public const int SigKill = 9;
        public const int SigTerm = 15;
        public const int NoSuchProcess = 3;
        public const int FunctionNotImplemented = 38;

        [LibraryImport("libc", EntryPoint = "pidfd_open", SetLastError = true)]
        public static partial int PidfdOpen(int processId, uint flags);

        [LibraryImport("libc", EntryPoint = "pidfd_send_signal", SetLastError = true)]
        public static partial int PidfdSendSignal(int pidfd, int signal, nint signalInfo, uint flags);
    }
}
