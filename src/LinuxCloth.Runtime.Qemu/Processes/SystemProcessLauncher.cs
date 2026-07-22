using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LinuxCloth.Runtime.Qemu.Processes;

public sealed partial class SystemProcessLauncher : IProcessLauncher
{
    private static readonly TimeSpan IdentityTransitionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdentityPollInterval = TimeSpan.FromMilliseconds(10);
    private const int MaximumIdentityProcessTreeSize = 64;

    public async Task<IManagedProcess> StartAsync(
        ProcessSpec spec,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        cancellationToken.ThrowIfCancellationRequested();

        var process = new Process
        {
            StartInfo = CreateStartInfo(spec),
            EnableRaisingEvents = true,
        };

        var started = false;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process '{spec.FileName}'.");
            }

            started = true;
            var identified = await WaitForIdentityAsync(process, spec, cancellationToken).ConfigureAwait(false);
            return new SystemManagedProcess(process, identified.Process, spec, identified.Identity);
        }
        catch
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
            throw;
        }
    }

    private static async Task<IdentifiedProcess> WaitForIdentityAsync(
        Process process,
        ProcessSpec spec,
        CancellationToken cancellationToken)
    {
        var expectedExecutable = spec.IdentityExecutablePath;
        if (expectedExecutable is null)
        {
            return new IdentifiedProcess(process, LinuxProcessIdentity.Read(process));
        }

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < IdentityTransitionTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Process '{spec.FileName}' exited before becoming '{expectedExecutable}'.");
            }

            try
            {
                var identified = FindExpectedProcess(process, expectedExecutable);
                if (identified is not null)
                {
                    return identified;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                InvalidDataException or
                IOException)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"Process '{spec.FileName}' exited before its final identity could be captured.",
                        exception);
                }
            }

            await Task.Delay(IdentityPollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Process '{spec.FileName}' did not become '{expectedExecutable}' within {IdentityTransitionTimeout}.");
    }

    private static IdentifiedProcess? FindExpectedProcess(
        Process root,
        string expectedExecutable)
    {
        var pending = new Queue<(int ProcessId, int? ParentProcessId)>();
        var visited = new HashSet<int>();
        pending.Enqueue((root.Id, null));

        while (pending.Count > 0)
        {
            var (processId, parentProcessId) = pending.Dequeue();
            if (!visited.Add(processId))
            {
                continue;
            }

            if (visited.Count > MaximumIdentityProcessTreeSize)
            {
                throw new InvalidDataException(
                    $"Process '{root.Id}' exceeded the bounded identity process-tree size.");
            }

            Process? candidate = null;
            var retainCandidate = false;
            try
            {
                candidate = processId == root.Id
                    ? root
                    : Process.GetProcessById(processId);
                var snapshot = LinuxProcessIdentity.ReadSnapshot(candidate);
                if (parentProcessId is not null && snapshot.ParentProcessId != parentProcessId)
                {
                    continue;
                }

                if (string.Equals(
                        snapshot.Identity.ExecutablePath,
                        expectedExecutable,
                        StringComparison.Ordinal))
                {
                    retainCandidate = true;
                    return new IdentifiedProcess(candidate, snapshot.Identity);
                }

                foreach (var childProcessId in ReadChildProcessIds(processId))
                {
                    pending.Enqueue((childProcessId, processId));
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                InvalidDataException or
                IOException or
                InvalidOperationException)
            {
                // The process tree may change while it is sampled; retry from the live root.
            }
            finally
            {
                if (!retainCandidate && candidate is not null && candidate.Id != root.Id)
                {
                    candidate.Dispose();
                }
            }
        }

        return null;
    }

    private static IEnumerable<int> ReadChildProcessIds(int processId)
    {
        var contents = File.ReadAllText($"/proc/{processId}/task/{processId}/children");
        foreach (var value in contents.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(
                    value,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var childProcessId) ||
                childProcessId <= 0)
            {
                throw new InvalidDataException($"Could not parse a child PID for process {processId}.");
            }

            yield return childProcessId;
        }
    }

    private static ProcessStartInfo CreateStartInfo(ProcessSpec spec)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = spec.StandardOutputPath is not null,
            RedirectStandardError = spec.StandardErrorPath is not null,
            CreateNoWindow = true,
        };

        if (startInfo.RedirectStandardOutput && spec.StandardOutputEncoding is not null)
        {
            startInfo.StandardOutputEncoding = spec.StandardOutputEncoding;
        }

        if (startInfo.RedirectStandardError && spec.StandardErrorEncoding is not null)
        {
            startInfo.StandardErrorEncoding = spec.StandardErrorEncoding;
        }

        if (!spec.InheritEnvironment)
        {
            startInfo.Environment.Clear();
        }

        if (spec.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = spec.WorkingDirectory;
        }

        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in spec.Environment)
        {
            startInfo.Environment[name] = value;
        }

        return startInfo;
    }

    private sealed class SystemManagedProcess : IManagedProcess
    {
        private const int MaximumLogCharacters = 1024 * 1024;
        private readonly Process _hostProcess;
        private readonly Process _payloadProcess;
        private readonly Task _standardOutputPump;
        private readonly Task _standardErrorPump;
        private bool _disposed;

        public SystemManagedProcess(
            Process hostProcess,
            Process payloadProcess,
            ProcessSpec spec,
            ProcessIdentity identity)
        {
            _hostProcess = hostProcess;
            _payloadProcess = payloadProcess;
            Identity = identity;
            _standardOutputPump = spec.StandardOutputPath is null
                ? Task.CompletedTask
                : PumpLogAsync(hostProcess.StandardOutput, spec.StandardOutputPath);
            _standardErrorPump = spec.StandardErrorPath is null
                ? Task.CompletedTask
                : PumpLogAsync(hostProcess.StandardError, spec.StandardErrorPath);
        }

        public int Id => Identity.ProcessId;

        public ProcessIdentity Identity { get; }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _payloadProcess.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            await _hostProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(_standardOutputPump, _standardErrorPump).WaitAsync(cancellationToken).ConfigureAwait(false);
            return _hostProcess.ExitCode;
        }

        public Task TerminateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasExited)
            {
                return Task.CompletedTask;
            }

            if (OperatingSystem.IsLinux())
            {
                if (NativeMethods.Kill(_payloadProcess.Id, NativeMethods.SigTerm) != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != NativeMethods.NoSuchProcess)
                    {
                        throw new IOException($"Failed to send SIGTERM to process {_payloadProcess.Id}; errno={error}.");
                    }
                }
            }
            else
            {
                _payloadProcess.CloseMainWindow();
            }

            return Task.CompletedTask;
        }

        public Task KillAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasExited)
            {
                _payloadProcess.Kill(entireProcessTree: true);
            }

            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!HasExited)
            {
                await KillAsync(CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                await WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The process was never associated or was already disposed.
            }

            if (_payloadProcess.Id != _hostProcess.Id)
            {
                _payloadProcess.Dispose();
            }

            _hostProcess.Dispose();
        }

        private static async Task PumpLogAsync(StreamReader reader, string path)
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await using var stream = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var writer = new StreamWriter(stream) { AutoFlush = true };
            var buffer = new char[4096];
            var written = 0;
            while (true)
            {
                var count = await reader.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0)
                {
                    break;
                }

                var allowed = Math.Min(count, MaximumLogCharacters - written);
                if (allowed > 0)
                {
                    await writer.WriteAsync(buffer.AsMemory(0, allowed)).ConfigureAwait(false);
                    written += allowed;
                }
            }

            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(fullPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    private sealed record IdentifiedProcess(Process Process, ProcessIdentity Identity);

    private static partial class NativeMethods
    {
        public const int SigTerm = 15;
        public const int NoSuchProcess = 3;

        [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
        public static partial int Kill(int processId, int signal);
    }
}

public static class LinuxProcessIdentity
{
    public static ProcessIdentity Read(Process process) => ReadSnapshot(process).Identity;

    internal static LinuxProcessSnapshot ReadSnapshot(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsLinux())
        {
            return new LinuxProcessSnapshot(
                new ProcessIdentity(
                    process.Id,
                    string.Empty,
                    process.StartTime.ToUniversalTime().Ticks,
                    process.MainModule?.FileName ?? process.ProcessName),
                ParentProcessId: 0);
        }

        var bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
        var stat = File.ReadAllText($"/proc/{process.Id}/stat");
        var closingParenthesis = stat.LastIndexOf(')');
        if (closingParenthesis < 0)
        {
            throw new InvalidDataException($"Could not parse /proc/{process.Id}/stat.");
        }

        var fields = stat[(closingParenthesis + 2)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length <= 19 ||
            !int.TryParse(fields[1], System.Globalization.CultureInfo.InvariantCulture, out var parentProcessId) ||
            !long.TryParse(fields[19], System.Globalization.CultureInfo.InvariantCulture, out var startTicks))
        {
            throw new InvalidDataException($"Could not read process identity fields for PID {process.Id}.");
        }

        var executable = File.ResolveLinkTarget($"/proc/{process.Id}/exe", returnFinalTarget: true)?.FullName
            ?? throw new InvalidDataException($"Could not resolve executable for PID {process.Id}.");
        return new LinuxProcessSnapshot(
            new ProcessIdentity(process.Id, bootId, startTicks, executable),
            parentProcessId);
    }
}

internal sealed record LinuxProcessSnapshot(ProcessIdentity Identity, int ParentProcessId);
