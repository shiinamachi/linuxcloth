using LinuxCloth.Core;
using LinuxCloth.Runtime.Qemu.Processes;

namespace LinuxCloth.Runtime.Qemu.Sessions;

internal sealed class SessionRecordJournal
{
    private readonly string _baseSha256;
    private readonly string _bootId;
    private readonly string _imageId;
    private readonly SessionPaths _paths;
    private readonly Dictionary<string, ProcessIdentity> _processes = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<ServiceId> _serviceIds;
    private readonly SessionRecordStore _store;

    public SessionRecordJournal(
        SessionRecordStore store,
        SessionPaths paths,
        string bootId,
        string imageId,
        string baseSha256,
        IReadOnlyList<ServiceId> serviceIds,
        SessionState initialState)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _bootId = bootId;
        _imageId = imageId;
        _baseSha256 = baseSha256;
        _serviceIds = serviceIds?.ToArray() ?? throw new ArgumentNullException(nameof(serviceIds));
        CurrentState = initialState;

        _ = CreateRecord(initialState);
    }

    public SessionState CurrentState { get; private set; }

    public Task InitializeAsync(CancellationToken cancellationToken) =>
        PersistAsync(CurrentState, cancellationToken);

    public async Task AddProcessAsync(
        string processName,
        IManagedProcess process,
        SessionState state,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        ArgumentNullException.ThrowIfNull(process);
        if (!_processes.TryAdd(processName, process.Identity))
        {
            throw new InvalidOperationException($"Process identity '{processName}' is already persisted.");
        }

        await PersistAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public Task TransitionAsync(SessionState state, CancellationToken cancellationToken) =>
        PersistAsync(state, cancellationToken);

    public async Task TryMarkFailedAsync(ICollection<Exception> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (CurrentState == SessionState.Failed ||
            !SessionStateTransitions.CanTransition(CurrentState, SessionState.Failed))
        {
            return;
        }

        try
        {
            await PersistAsync(SessionState.Failed, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private async Task PersistAsync(SessionState state, CancellationToken cancellationToken)
    {
        SessionStateTransitions.EnsureCanTransition(CurrentState, state);
        var record = CreateRecord(state);
        await _store.WriteAsync(_paths, record, cancellationToken).ConfigureAwait(false);
        CurrentState = state;
    }

    private SessionRecord CreateRecord(SessionState state) =>
        new(
            _paths.SessionId,
            _bootId,
            state,
            _imageId,
            _baseSha256,
            _serviceIds,
            _processes);
}
