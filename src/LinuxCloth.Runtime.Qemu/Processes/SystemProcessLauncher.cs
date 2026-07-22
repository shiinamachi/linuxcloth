using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LinuxCloth.Runtime.Qemu.Processes;

public sealed partial class SystemProcessLauncher : IProcessLauncher
{
    private static readonly TimeSpan IdentityTransitionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IdentityPollInterval = TimeSpan.FromMilliseconds(10);

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
            var identity = await WaitForIdentityAsync(process, spec, cancellationToken).ConfigureAwait(false);
            return new SystemManagedProcess(process, spec, identity);
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

    private static async Task<ProcessIdentity> WaitForIdentityAsync(
        Process process,
        ProcessSpec spec,
        CancellationToken cancellationToken)
    {
        var expectedExecutable = spec.IdentityExecutablePath;
        if (expectedExecutable is null)
        {
            return LinuxProcessIdentity.Read(process);
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
                var identity = LinuxProcessIdentity.Read(process);
                if (string.Equals(identity.ExecutablePath, expectedExecutable, StringComparison.Ordinal))
                {
                    return identity;
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
        private readonly Process _process;
        private readonly Task _standardOutputPump;
        private readonly Task _standardErrorPump;
        private bool _disposed;

        public SystemManagedProcess(Process process, ProcessSpec spec, ProcessIdentity identity)
        {
            _process = process;
            Identity = identity;
            _standardOutputPump = spec.StandardOutputPath is null
                ? Task.CompletedTask
                : PumpLogAsync(process.StandardOutput, spec.StandardOutputPath);
            _standardErrorPump = spec.StandardErrorPath is null
                ? Task.CompletedTask
                : PumpLogAsync(process.StandardError, spec.StandardErrorPath);
        }

        public int Id => _process.Id;

        public ProcessIdentity Identity { get; }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(_standardOutputPump, _standardErrorPump).WaitAsync(cancellationToken).ConfigureAwait(false);
            return _process.ExitCode;
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
                if (NativeMethods.Kill(_process.Id, NativeMethods.SigTerm) != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != NativeMethods.NoSuchProcess)
                    {
                        throw new IOException($"Failed to send SIGTERM to process {_process.Id}; errno={error}.");
                    }
                }
            }
            else
            {
                _process.CloseMainWindow();
            }

            return Task.CompletedTask;
        }

        public Task KillAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasExited)
            {
                _process.Kill(entireProcessTree: true);
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

            _process.Dispose();
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
    public static ProcessIdentity Read(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsLinux())
        {
            return new ProcessIdentity(
                process.Id,
                string.Empty,
                process.StartTime.ToUniversalTime().Ticks,
                process.MainModule?.FileName ?? process.ProcessName);
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
            !long.TryParse(fields[19], System.Globalization.CultureInfo.InvariantCulture, out var startTicks))
        {
            throw new InvalidDataException($"Could not read process start ticks for PID {process.Id}.");
        }

        var executable = File.ResolveLinkTarget($"/proc/{process.Id}/exe", returnFinalTarget: true)?.FullName
            ?? throw new InvalidDataException($"Could not resolve executable for PID {process.Id}.");
        return new ProcessIdentity(process.Id, bootId, startTicks, executable);
    }
}
