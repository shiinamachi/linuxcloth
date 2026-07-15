using System.Globalization;
using System.Text;

namespace LinuxCloth.GuestBridge;

internal enum DiagnosticEvent
{
    Started,
    UnsupportedPlatform,
    AlreadyRunning,
    ConfigurationNotFound,
    ConfigurationRejected,
    ConfigurationAmbiguous,
    BootstrapStarted,
    BootstrapCompleted,
    BootstrapFailed,
    BootstrapLaunchFailed,
    Cancelled,
    UnexpectedFailure,
}

internal interface IDiagnosticLog
{
    void Write(DiagnosticEvent diagnosticEvent);
}

internal sealed class BoundedFileDiagnosticLog : IDiagnosticLog
{
    internal const int MaximumLogBytes = 256 * 1024;

    private readonly string _path;
    private readonly int _maximumLogBytes;
    private readonly object _sync = new();

    public BoundedFileDiagnosticLog(
        string path,
        int maximumLogBytes = MaximumLogBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumLogBytes < 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLogBytes),
                "The maximum diagnostics size must be at least 128 bytes.");
        }

        _path = Path.GetFullPath(path);
        _maximumLogBytes = maximumLogBytes;
    }

    public void Write(DiagnosticEvent diagnosticEvent)
    {
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:O} {diagnosticEvent}\n");
        var bytes = Encoding.ASCII.GetBytes(line);

        lock (_sync)
        {
            try
            {
                WriteCore(bytes);
            }
            catch (IOException)
            {
                // Diagnostics must never prevent the bridge from running.
            }
            catch (UnauthorizedAccessException)
            {
                // Diagnostics must never prevent the bridge from running.
            }
        }
    }

    private void WriteCore(byte[] bytes)
    {
        if (bytes.Length > _maximumLogBytes)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path)
            ?? throw new IOException("The diagnostics path has no parent directory.");
        Directory.CreateDirectory(directory);

        var backupPath = _path + ".1";
        DeleteOversizedBackup(backupPath, _maximumLogBytes);

        var currentLength = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        if (currentLength > _maximumLogBytes)
        {
            File.Delete(_path);
            currentLength = 0;
        }

        if (currentLength + bytes.Length > _maximumLogBytes)
        {
            File.Move(_path, backupPath, overwrite: true);
        }

        using var stream = new FileStream(
            _path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void DeleteOversizedBackup(string backupPath, int maximumLogBytes)
    {
        if (File.Exists(backupPath) && new FileInfo(backupPath).Length > maximumLogBytes)
        {
            File.Delete(backupPath);
        }
    }
}

internal sealed class NullDiagnosticLog : IDiagnosticLog
{
    public static NullDiagnosticLog Instance { get; } = new();

    private NullDiagnosticLog()
    {
    }

    public void Write(DiagnosticEvent diagnosticEvent)
    {
    }
}
