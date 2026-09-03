using LiteTerm.Core.Connections;
using LiteTerm.Core.Terminal;

namespace LiteTerm.Tests;

public sealed class TerminalAutoReconnectPolicyTests
{
    [Fact]
    public void CanSchedule_AllowsRetryableFailureAfterSuccessfulConnection()
    {
        Assert.True(TerminalAutoReconnectPolicy.CanSchedule(
            enabled: true,
            ConnectionState.Failed,
            hasConnectionSnapshot: true,
            hasSuccessfulConnection: true,
            new ConnectionFailure(ConnectionFailureKind.ConnectionLost),
            completedAttempts: 0));
    }

    [Theory]
    [InlineData(false, ConnectionState.Failed, true, true, ConnectionFailureKind.ConnectionLost, 0)]
    [InlineData(true, ConnectionState.Disconnected, true, true, ConnectionFailureKind.ConnectionLost, 0)]
    [InlineData(true, ConnectionState.Failed, false, true, ConnectionFailureKind.ConnectionLost, 0)]
    [InlineData(true, ConnectionState.Failed, true, false, ConnectionFailureKind.ConnectionLost, 0)]
    [InlineData(true, ConnectionState.Failed, true, true, ConnectionFailureKind.AuthenticationRejected, 0)]
    [InlineData(true, ConnectionState.Failed, true, true, ConnectionFailureKind.HostKeyRejected, 0)]
    [InlineData(true, ConnectionState.Failed, true, true, ConnectionFailureKind.PermissionDenied, 0)]
    [InlineData(true, ConnectionState.Failed, true, true, ConnectionFailureKind.ConnectionLost, 3)]
    public void CanSchedule_RejectsUnsafeOrExhaustedConditions(
        bool enabled,
        ConnectionState state,
        bool hasConnectionSnapshot,
        bool hasSuccessfulConnection,
        ConnectionFailureKind failureKind,
        int completedAttempts)
    {
        Assert.False(TerminalAutoReconnectPolicy.CanSchedule(
            enabled,
            state,
            hasConnectionSnapshot,
            hasSuccessfulConnection,
            new ConnectionFailure(failureKind),
            completedAttempts));
    }

    [Fact]
    public void CanSchedule_RequiresFailureDetails()
    {
        Assert.False(TerminalAutoReconnectPolicy.CanSchedule(
            enabled: true,
            ConnectionState.Failed,
            hasConnectionSnapshot: true,
            hasSuccessfulConnection: true,
            failure: null,
            completedAttempts: 0));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 5)]
    [InlineData(3, 10)]
    public void GetDelay_UsesBoundedIncreasingDelays(int nextAttempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), TerminalAutoReconnectPolicy.GetDelay(nextAttempt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void GetDelay_RejectsAttemptsOutsidePolicy(int nextAttempt)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalAutoReconnectPolicy.GetDelay(nextAttempt));
    }
}
