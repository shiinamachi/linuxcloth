namespace LinuxCloth.Core;

public enum SessionState
{
    Idle,
    Validating,
    PreparingOverlay,
    PreparingConfigDisk,
    StartingNetwork,
    StartingVm,
    WaitingForGuest,
    Running,
    Stopping,
    Cleaning,
    Completed,
    Failed,
}

public static class SessionStateTransitions
{
    private static readonly Dictionary<SessionState, HashSet<SessionState>> Allowed =
        new()
        {
            [SessionState.Idle] = Set(SessionState.Validating, SessionState.Cleaning),
            [SessionState.Validating] = Set(SessionState.PreparingOverlay, SessionState.Failed),
            [SessionState.PreparingOverlay] = Set(SessionState.PreparingConfigDisk, SessionState.Failed),
            [SessionState.PreparingConfigDisk] = Set(SessionState.StartingNetwork, SessionState.StartingVm, SessionState.Failed),
            [SessionState.StartingNetwork] = Set(SessionState.StartingVm, SessionState.Failed),
            [SessionState.StartingVm] = Set(SessionState.WaitingForGuest, SessionState.Failed),
            [SessionState.WaitingForGuest] = Set(SessionState.Running, SessionState.Stopping, SessionState.Failed),
            [SessionState.Running] = Set(SessionState.Stopping, SessionState.Failed),
            [SessionState.Stopping] = Set(SessionState.Cleaning, SessionState.Failed),
            [SessionState.Failed] = Set(SessionState.Cleaning),
            [SessionState.Cleaning] = Set(SessionState.Completed),
            [SessionState.Completed] = Set(SessionState.Cleaning),
        };

    public static bool CanTransition(SessionState from, SessionState to) =>
        from == to || (Allowed.TryGetValue(from, out var transitions) && transitions.Contains(to));

    public static void EnsureCanTransition(SessionState from, SessionState to)
    {
        if (!CanTransition(from, to))
        {
            throw new InvalidOperationException($"Session cannot transition from {from} to {to}.");
        }
    }

    private static HashSet<SessionState> Set(params SessionState[] states) => states.ToHashSet();
}
