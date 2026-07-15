using System.Text;

namespace LinuxCloth.Runtime.Qemu.Sessions;

public sealed record SessionPaths
{
    private const int SafeUnixSocketPathBytes = 100;

    private SessionPaths(string runtimeRoot, Guid sessionId)
    {
        RuntimeRoot = runtimeRoot;
        SessionId = sessionId;
        SessionDirectory = Path.Combine(runtimeRoot, "sessions", sessionId.ToString("N"));
        OverlayPath = Path.Combine(SessionDirectory, "overlay.qcow2");
        ConfigDirectory = Path.Combine(SessionDirectory, "config");
        OvmfVariablesPath = Path.Combine(SessionDirectory, "ovmf-vars.fd");
        SwtpmStateDirectory = Path.Combine(SessionDirectory, "swtpm");
        SwtpmSocketPath = Path.Combine(SessionDirectory, "tpm.sock");
        QmpSocketPath = Path.Combine(SessionDirectory, "qmp.sock");
        SpiceSocketPath = Path.Combine(SessionDirectory, "spice.sock");
        GuestBridgeSocketPath = Path.Combine(SessionDirectory, "guest.sock");
        PasstSocketPath = Path.Combine(SessionDirectory, "net.sock");
        SessionRecordPath = Path.Combine(SessionDirectory, "session.json");

        ValidateSocketPath(SwtpmSocketPath);
        ValidateSocketPath(QmpSocketPath);
        ValidateSocketPath(SpiceSocketPath);
        ValidateSocketPath(GuestBridgeSocketPath);
        ValidateSocketPath(PasstSocketPath);
    }

    public string RuntimeRoot { get; }

    public Guid SessionId { get; }

    public string SessionDirectory { get; }

    public string OverlayPath { get; }

    public string ConfigDirectory { get; }

    public string OvmfVariablesPath { get; }

    public string SwtpmStateDirectory { get; }

    public string SwtpmSocketPath { get; }

    public string QmpSocketPath { get; }

    public string SpiceSocketPath { get; }

    public string GuestBridgeSocketPath { get; }

    public string PasstSocketPath { get; }

    public string SessionRecordPath { get; }

    public static SessionPaths Create(string runtimeRoot, Guid sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        if (!Path.IsPathFullyQualified(runtimeRoot))
        {
            throw new ArgumentException("The runtime root must be an absolute path.", nameof(runtimeRoot));
        }

        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("The session identifier cannot be empty.", nameof(sessionId));
        }

        return new SessionPaths(Path.GetFullPath(runtimeRoot), sessionId);
    }

    public void CreateDirectories()
    {
        Directory.CreateDirectory(SessionDirectory);
        Directory.CreateDirectory(ConfigDirectory);

        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(SessionDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(ConfigDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void ValidateSocketPath(string path)
    {
        if (Encoding.UTF8.GetByteCount(path) > SafeUnixSocketPathBytes)
        {
            throw new PathTooLongException($"Unix socket path exceeds {SafeUnixSocketPathBytes} UTF-8 bytes: {path}");
        }
    }
}
