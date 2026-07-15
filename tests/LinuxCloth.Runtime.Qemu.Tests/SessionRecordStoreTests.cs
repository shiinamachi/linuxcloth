using System.Collections.Concurrent;
using System.Text.Json;
using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Processes;
using LinuxCloth.Runtime.Qemu.Sessions;

namespace LinuxCloth.Runtime.Qemu.Tests;

public sealed class SessionRecordStoreTests : IDisposable
{
    private const string BootId = "11111111-2222-3333-4444-555555555555";
    private readonly string _runtimeRoot = Path.Combine(Path.GetTempPath(), $"lcr-{Guid.NewGuid():N}"[..16]);
    private readonly SessionRecordStore _store = new();

    [Fact]
    public async Task RoundTripsStrictSchemaWithPrivateMode()
    {
        var paths = CreatePaths();
        var record = CreateRecord(paths.SessionId);

        await _store.WriteAsync(paths, record);
        var loaded = await _store.ReadAsync(paths);

        Assert.Equal(record.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(record.SessionId, loaded.SessionId);
        Assert.Equal(record.BootId, loaded.BootId);
        Assert.Equal(record.State, loaded.State);
        Assert.Equal(record.ImageId, loaded.ImageId);
        Assert.Equal(record.BaseSha256, loaded.BaseSha256);
        Assert.Equal(record.ServiceIds, loaded.ServiceIds);
        Assert.Equal(record.Processes[SessionProcessNames.Qemu], loaded.Processes[SessionProcessNames.Qemu]);

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(paths.SessionRecordPath));
        Assert.Equal(
            ["schemaVersion", "sessionId", "bootId", "state", "imageId", "baseSha256", "serviceIds", "processes"],
            document.RootElement.EnumerateObject().Select(static property => property.Name));
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(paths.SessionRecordPath));
        }
    }

    [Fact]
    public async Task RejectsUnknownAndDuplicateProperties()
    {
        var paths = CreatePaths();
        await _store.WriteAsync(paths, CreateRecord(paths.SessionId));
        var original = await File.ReadAllTextAsync(paths.SessionRecordPath);

        var unknown = original.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"unexpected\": true,",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(paths.SessionRecordPath, unknown);
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.ReadAsync(paths));

        var duplicate = original.Replace(
            "\"state\": \"Running\",",
            "\"state\": \"Running\",\n  \"state\": \"Failed\",",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(paths.SessionRecordPath, duplicate);
        await Assert.ThrowsAsync<InvalidDataException>(() => _store.ReadAsync(paths));
    }

    [Fact]
    public async Task RejectsTamperedProcessIdentity()
    {
        var paths = CreatePaths();
        await _store.WriteAsync(paths, CreateRecord(paths.SessionId));
        var json = await File.ReadAllTextAsync(paths.SessionRecordPath);
        var marker = $"\"bootId\": \"{BootId}\"";
        var processBootIdOffset = json.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.True(processBootIdOffset > 0);
        var tampered = string.Concat(
            json.AsSpan(0, processBootIdOffset),
            "\"bootId\": \"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\"",
            json.AsSpan(processBootIdOffset + marker.Length));
        await File.WriteAllTextAsync(paths.SessionRecordPath, tampered);

        await Assert.ThrowsAsync<InvalidDataException>(() => _store.ReadAsync(paths));
    }

    [Fact]
    public async Task AtomicReplacementNeverExposesPartialJson()
    {
        var paths = CreatePaths();
        await _store.WriteAsync(paths, CreateRecord(paths.SessionId, imageId: "image-a"));
        var failures = new ConcurrentQueue<Exception>();
        var writing = true;
        var reader = Task.Run(async () =>
        {
            while (Volatile.Read(ref writing))
            {
                try
                {
                    var record = await _store.ReadAsync(paths);
                    Assert.True(record.ImageId is "image-a" or "image-b");
                }
                catch (Exception exception)
                {
                    failures.Enqueue(exception);
                }
            }
        });

        for (var index = 0; index < 25; index++)
        {
            await _store.WriteAsync(
                paths,
                CreateRecord(paths.SessionId, imageId: index % 2 == 0 ? "image-b" : "image-a"));
        }

        Volatile.Write(ref writing, false);
        await reader;

        Assert.Empty(failures);
        Assert.Empty(Directory.EnumerateFiles(paths.SessionDirectory, ".session.json.*.tmp"));
        _ = await _store.ReadAsync(paths);
    }

    [Fact]
    public async Task CancelledReplacementPreservesPreviousRecord()
    {
        var paths = CreatePaths();
        await _store.WriteAsync(paths, CreateRecord(paths.SessionId, imageId: "image-a"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _store.WriteAsync(
                paths,
                CreateRecord(paths.SessionId, imageId: "image-b"),
                cancellation.Token));

        Assert.Equal("image-a", (await _store.ReadAsync(paths)).ImageId);
        Assert.Empty(Directory.EnumerateFiles(paths.SessionDirectory, ".session.json.*.tmp"));
    }

    [Fact]
    public async Task RejectsRecordBeyondFixedByteLimit()
    {
        var paths = CreatePaths();
        await File.WriteAllBytesAsync(paths.SessionRecordPath, new byte[(64 * 1024) + 1]);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                paths.SessionRecordPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => _store.ReadAsync(paths));
    }

    [Fact]
    public void RejectsRunningRecordWithoutPersistedQemuIdentity()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new SessionRecord(
                Guid.NewGuid(),
                BootId,
                SessionState.Running,
                "test-image",
                new string('a', 64),
                [ServiceId.Parse("WooriBank")]));

        Assert.Contains("QEMU", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_runtimeRoot))
        {
            Directory.Delete(_runtimeRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private SessionPaths CreatePaths()
    {
        var paths = SessionPaths.Create(_runtimeRoot, Guid.NewGuid());
        paths.CreateDirectories();
        return paths;
    }

    private static SessionRecord CreateRecord(
        Guid sessionId,
        SessionState state = SessionState.Running,
        string imageId = "test-image") =>
        new(
            sessionId,
            BootId,
            state,
            imageId,
            new string('a', 64),
            [ServiceId.Parse("WooriBank")],
            new Dictionary<string, ProcessIdentity>(StringComparer.Ordinal)
            {
                [SessionProcessNames.Qemu] = new(101, BootId, 202, "/usr/bin/qemu-system-x86_64"),
            });
}
