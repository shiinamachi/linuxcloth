using Tmds.DBus.Protocol;

namespace LinuxCloth.Desktop.Setup;

public sealed class PackageKitDbusClient : IPackageKitClient
{
    private const string ServiceName = "org.freedesktop.PackageKit";
    private const string ControlPath = "/org/freedesktop/PackageKit";
    private const string ControlInterface = "org.freedesktop.PackageKit";
    private const string TransactionInterface = "org.freedesktop.PackageKit.Transaction";

    private readonly DBusConnection _connection;
    private bool _disposed;

    public PackageKitDbusClient()
    {
        _connection = new DBusConnection(
            DBusAddress.System ??
            throw new PlatformNotSupportedException("시스템 D-Bus 주소를 확인할 수 없습니다."));
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            var active = await _connection.ListServicesAsync().WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (active.Contains(ServiceName, StringComparer.Ordinal))
            {
                return true;
            }

            var activatable = await _connection.ListActivatableServicesAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return activatable.Contains(ServiceName, StringComparer.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DBusExceptionBase)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<PackageKitPackage>> ResolveAsync(
        IReadOnlyList<string> packageNames,
        CancellationToken cancellationToken = default)
    {
        ValidateValues(packageNames, "패키지 이름");
        var capture = await RunTransactionAsync(
                (path, token) => CallTransactionAsync(
                    path,
                    "Resolve",
                    "tas",
                    (ref MessageWriter writer) =>
                    {
                        writer.WriteUInt64(PackageKitEnums.FilterNone);
                        writer.WriteArray(packageNames.ToArray());
                    },
                    token),
                progress: null,
                captureDetails: false,
                cancellationToken)
            .ConfigureAwait(false);
        return capture.Packages;
    }

    public async Task<IReadOnlyList<PackageKitPackage>> SimulateInstallAsync(
        IReadOnlyList<string> packageIds,
        CancellationToken cancellationToken = default)
    {
        ValidateValues(packageIds, "패키지 ID");
        var capture = await RunTransactionAsync(
                (path, token) => CallTransactionAsync(
                    path,
                    "InstallPackages",
                    "tas",
                    (ref MessageWriter writer) =>
                    {
                        writer.WriteUInt64(
                            PackageKitEnums.TransactionOnlyTrusted |
                            PackageKitEnums.TransactionSimulate);
                        writer.WriteArray(packageIds.ToArray());
                    },
                    token),
                progress: null,
                captureDetails: false,
                cancellationToken)
            .ConfigureAwait(false);
        return capture.Packages;
    }

    public async Task<IReadOnlyDictionary<string, PackageKitDetails>> GetDetailsAsync(
        IReadOnlyList<string> packageIds,
        CancellationToken cancellationToken = default)
    {
        ValidateValues(packageIds, "패키지 ID");
        var capture = await RunTransactionAsync(
                (path, token) => CallTransactionAsync(
                    path,
                    "GetDetails",
                    "as",
                    (ref MessageWriter writer) => writer.WriteArray(packageIds.ToArray()),
                    token),
                progress: null,
                captureDetails: true,
                cancellationToken)
            .ConfigureAwait(false);
        return capture.Details;
    }

    public async Task InstallAsync(
        IReadOnlyList<string> packageIds,
        IProgress<PackageInstallProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ValidateValues(packageIds, "패키지 ID");
        _ = await RunTransactionAsync(
                (path, token) => CallTransactionAsync(
                    path,
                    "InstallPackages",
                    "tas",
                    (ref MessageWriter writer) =>
                    {
                        writer.WriteUInt64(PackageKitEnums.TransactionOnlyTrusted);
                        writer.WriteArray(packageIds.ToArray());
                    },
                    token),
                progress,
                captureDetails: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _connection.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<TransactionCapture> RunTransactionAsync(
        Func<ObjectPath, CancellationToken, Task> start,
        IProgress<PackageInstallProgress>? progress,
        bool captureDetails,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var path = await CreateTransactionAsync(cancellationToken).ConfigureAwait(false);
        await SetHintsAsync(path, progress is not null, cancellationToken).ConfigureAwait(false);
        var capture = new TransactionCapture();
        var observers = new List<IDisposable>();
        try
        {
            observers.Add(await WatchAsync(
                    path,
                    "Package",
                    static (Message message, object? state) =>
                    {
                        var reader = message.GetBodyReader();
                        return new PackageKitPackage(
                            reader.ReadUInt32(),
                            reader.ReadString(),
                            reader.ReadString());
                    },
                    notification =>
                    {
                        if (!capture.HandleNotification(notification) || !notification.HasValue)
                        {
                            return;
                        }

                        var package = notification.Value;
                        capture.AddPackage(package);
                        var id = package.ParseId();
                        progress?.Report(
                            new PackageInstallProgress(
                                PackageStatus(package.Info),
                                id.Name));
                    })
                .ConfigureAwait(false));
            observers.Add(await WatchAsync(
                    path,
                    "ErrorCode",
                    static (Message message, object? state) =>
                    {
                        var reader = message.GetBodyReader();
                        return (Code: reader.ReadUInt32(), Details: reader.ReadString());
                    },
                    notification =>
                    {
                        if (capture.HandleNotification(notification) && notification.HasValue)
                        {
                            capture.SetPackageKitError(notification.Value.Code, notification.Value.Details);
                        }
                    })
                .ConfigureAwait(false));
            observers.Add(await WatchAsync(
                    path,
                    "Finished",
                    static (Message message, object? state) =>
                    {
                        var reader = message.GetBodyReader();
                        return (Exit: reader.ReadUInt32(), Runtime: reader.ReadUInt32());
                    },
                    notification =>
                    {
                        if (capture.HandleNotification(notification) && notification.HasValue)
                        {
                            capture.Finish(notification.Value.Exit);
                        }
                    })
                .ConfigureAwait(false));
            if (captureDetails)
            {
                observers.Add(await WatchAsync(
                        path,
                        "Details",
                        static (Message message, object? state) =>
                            message.GetBodyReader().ReadDictionaryOfStringToVariantValue(),
                        notification =>
                        {
                            if (capture.HandleNotification(notification) && notification.HasValue)
                            {
                                capture.AddDetails(notification.Value);
                            }
                        })
                    .ConfigureAwait(false));
            }

            using var cancellationRegistration = cancellationToken.Register(
                () => _ = CancelTransactionIgnoringFailureAsync(path));
            await start(path, cancellationToken).ConfigureAwait(false);
            await capture.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return capture;
        }
        finally
        {
            foreach (var observer in observers)
            {
                observer.Dispose();
            }
        }
    }

    private ValueTask<IDisposable> WatchAsync<T>(
        ObjectPath path,
        string signal,
        MessageValueReader<T> reader,
        Action<Notification<T>> handler) =>
        _connection.WatchSignalAsync(
            ServiceName,
            path.ToString(),
            TransactionInterface,
            signal,
            reader,
            handler,
            ObserverFlags.None,
            emitOnCapturedContext: false,
            state: null);

    private async Task<ObjectPath> CreateTransactionAsync(CancellationToken cancellationToken)
    {
        MessageBuffer message;
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteMethodCallHeader(
                destination: ServiceName,
                path: ControlPath,
                @interface: ControlInterface,
                member: "CreateTransaction");
            message = writer.CreateMessage();
        }

        return await _connection.CallMethodAsync(
                message,
                static (Message reply, object? state) => reply.GetBodyReader().ReadObjectPath())
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private Task SetHintsAsync(
        ObjectPath path,
        bool isInteractive,
        CancellationToken cancellationToken)
    {
        string[] hints =
        [
            $"interactive={isInteractive.ToString().ToLowerInvariant()}",
            "background=false",
        ];
        return CallTransactionAsync(
            path,
            "SetHints",
            "as",
            (ref MessageWriter writer) => writer.WriteArray(hints),
            cancellationToken);
    }

    private async Task CallTransactionAsync(
        ObjectPath path,
        string member,
        string? signature,
        MessageWriterAction? writeBody,
        CancellationToken cancellationToken)
    {
        MessageBuffer message;
        {
            var writer = _connection.GetMessageWriter();
            try
            {
                writer.WriteMethodCallHeader(
                    destination: ServiceName,
                    path: path,
                    @interface: TransactionInterface,
                    signature: signature,
                    member: member);
                writeBody?.Invoke(ref writer);
                message = writer.CreateMessage();
            }
            finally
            {
                writer.Dispose();
            }
        }

        await _connection.CallMethodAsync(message)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task CancelTransactionIgnoringFailureAsync(ObjectPath path)
    {
        try
        {
            await CallTransactionAsync(
                    path,
                    "Cancel",
                    signature: null,
                    writeBody: null,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original cancellation remains the user-visible result.
        }
    }

    private static void ValidateValues(IReadOnlyList<string> values, string description)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count is <= 0 or > 512 || values.Any(value =>
                string.IsNullOrWhiteSpace(value) ||
                value.Length > 1024 ||
                value.Any(char.IsControl)))
        {
            throw new ArgumentException($"{description} 목록이 비어 있거나 허용 범위를 벗어났습니다.");
        }
    }

    private static string PackageStatus(uint info) => info switch
    {
        10 => "패키지를 다운로드하고 있습니다…",
        11 => "패키지를 업데이트하고 있습니다…",
        12 or 27 => "패키지를 설치하고 있습니다…",
        13 or 28 => "패키지를 제거하고 있습니다…",
        _ => "패키지 작업을 진행하고 있습니다…",
    };

    private delegate void MessageWriterAction(ref MessageWriter writer);

    private sealed class TransactionCapture
    {
        private readonly Dictionary<string, PackageKitDetails> _details =
            new(StringComparer.Ordinal);
        private readonly List<PackageKitPackage> _packages = [];
        private (uint Code, string Details)? _error;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<PackageKitPackage> Packages => _packages.ToArray();

        public IReadOnlyDictionary<string, PackageKitDetails> Details =>
            new Dictionary<string, PackageKitDetails>(_details, StringComparer.Ordinal);

        public bool HandleNotification<T>(Notification<T> notification)
        {
            if (notification.Exception is not null)
            {
                Completion.TrySetException(notification.Exception);
                return false;
            }

            return !notification.IsCompletion;
        }

        public void AddPackage(PackageKitPackage package) => _packages.Add(package);

        public void SetPackageKitError(uint code, string details) => _error = (code, details);

        public void AddDetails(Dictionary<string, VariantValue> values)
        {
            if (!values.TryGetValue("package_id", out var packageIdValue))
            {
                return;
            }

            var packageId = packageIdValue.GetString();
            var size = values.TryGetValue("size", out var sizeValue)
                ? sizeValue.GetUInt64()
                : 0;
            var summary = values.TryGetValue("summary", out var summaryValue)
                ? summaryValue.GetString()
                : null;
            _details[packageId] = new PackageKitDetails(packageId, size, summary);
        }

        public void Finish(uint exit)
        {
            if (_error is { } error)
            {
                Completion.TrySetException(
                    new PackageKitException(
                        $"PackageKit 오류 {error.Code}: {error.Details}"));
            }
            else if (exit != PackageKitEnums.ExitSuccess)
            {
                Completion.TrySetException(
                    new PackageKitException($"PackageKit 트랜잭션이 종료 코드 {exit}(으)로 실패했습니다."));
            }
            else
            {
                Completion.TrySetResult();
            }
        }
    }
}
