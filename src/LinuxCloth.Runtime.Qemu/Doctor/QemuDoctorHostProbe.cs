using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace LinuxCloth.Runtime.Qemu.Doctor;

public interface IQemuDoctorHostProbe
{
    bool IsLinux { get; }

    Architecture ProcessArchitecture { get; }

    string PlatformDescription { get; }

    HostProbeResult ProbeReadWriteDevice(string path);

    HostProbeResult ProbeUnixSocketDirectory(string path);
}

public sealed class SystemQemuDoctorHostProbe : IQemuDoctorHostProbe
{
    public bool IsLinux => OperatingSystem.IsLinux();

    public Architecture ProcessArchitecture => RuntimeInformation.ProcessArchitecture;

    public string PlatformDescription => RuntimeInformation.OSDescription;

    public HostProbeResult ProbeReadWriteDevice(string path)
    {
        if (!File.Exists(path))
        {
            return new HostProbeResult(
                false,
                $"KVM device '{path}' does not exist. Enable KVM and install the distribution's KVM support.");
        }

        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return handle.IsInvalid
                ? new HostProbeResult(false, $"KVM device '{path}' could not be opened read-write.")
                : new HostProbeResult(true, $"KVM device '{path}' is accessible read-write.");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return new HostProbeResult(
                false,
                $"KVM device '{path}' is not accessible read-write: {exception.Message} " +
                "Check the device permissions and group membership.");
        }
    }

    public HostProbeResult ProbeUnixSocketDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return new HostProbeResult(false, $"XDG runtime directory '{path}' must be an absolute path.");
        }

        if (!Directory.Exists(path))
        {
            return new HostProbeResult(
                false,
                $"XDG runtime directory '{path}' does not exist. " +
                "Start linuxcloth from a user session that defines XDG_RUNTIME_DIR.");
        }

        try
        {
            var runtimeDirectory = new DirectoryInfo(path);
            if (runtimeDirectory.LinkTarget is not null)
            {
                return new HostProbeResult(
                    false,
                    $"XDG runtime directory '{path}' must not be a symbolic link.");
            }

            if (OperatingSystem.IsLinux())
            {
                var expectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                if (File.GetUnixFileMode(path) != expectedMode)
                {
                    return new HostProbeResult(
                        false,
                        $"XDG runtime directory '{path}' must have mode 0700.");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new HostProbeResult(
                false,
                $"XDG runtime directory '{path}' could not be inspected: {exception.Message}");
        }

        var probeId = Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture)[..12];
        var probeDirectory = Path.Combine(path, $".lc-{probeId}");
        var socketPath = Path.Combine(probeDirectory, "s");

        try
        {
            Directory.CreateDirectory(probeDirectory);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    probeDirectory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(socketPath));
            return new HostProbeResult(true, $"Unix sockets can be created under '{path}'.");
        }
        catch (Exception exception) when (exception is IOException or
                                          UnauthorizedAccessException or
                                          SocketException or
                                          ArgumentException or
                                          NotSupportedException)
        {
            return new HostProbeResult(
                false,
                $"Unix sockets cannot be created under XDG runtime directory '{path}': {exception.Message}");
        }
        finally
        {
            TryDeleteSocket(socketPath);
            TryDeleteDirectory(probeDirectory);
        }
    }

    private static void TryDeleteSocket(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The bounded probe must not turn cleanup failure into a false launch failure.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The directory is uniquely named and contains no application or user data.
        }
    }
}
