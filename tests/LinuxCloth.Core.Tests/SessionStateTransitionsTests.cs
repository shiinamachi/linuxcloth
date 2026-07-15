using LinuxCloth.Core;

namespace LinuxCloth.Core.Tests;

public sealed class SessionStateTransitionsTests
{
    [Fact]
    public void HappyPathIsAllowed()
    {
        var path = new[]
        {
            SessionState.Idle,
            SessionState.Validating,
            SessionState.PreparingOverlay,
            SessionState.PreparingConfigDisk,
            SessionState.StartingNetwork,
            SessionState.StartingVm,
            SessionState.WaitingForGuest,
            SessionState.Running,
            SessionState.Stopping,
            SessionState.Cleaning,
            SessionState.Completed,
        };

        for (var index = 0; index < path.Length - 1; index++)
        {
            Assert.True(SessionStateTransitions.CanTransition(path[index], path[index + 1]));
        }
    }

    [Fact]
    public void SkippingPreparationIsRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => SessionStateTransitions.EnsureCanTransition(SessionState.Validating, SessionState.Running));
    }

    [Fact]
    public void CleanupCanBeRetriedAfterCompletion()
    {
        Assert.True(SessionStateTransitions.CanTransition(SessionState.Completed, SessionState.Cleaning));
    }
}
