using LiteTerm.Core.Connections;
using LiteTerm.Core.Terminal;

namespace LiteTerm.Tests;

public sealed class TerminalReconnectPolicyTests
{
    [Theory]
    [InlineData(ConnectionState.Disconnected, true, true)]
    [InlineData(ConnectionState.Failed, true, true)]
    [InlineData(ConnectionState.Connecting, true, false)]
    [InlineData(ConnectionState.Connected, true, false)]
    [InlineData(ConnectionState.Disconnecting, true, false)]
    [InlineData(ConnectionState.Disconnected, false, false)]
    [InlineData(ConnectionState.Failed, false, false)]
    public void CanReconnect_RequiresSnapshotAndInactiveSession(
        ConnectionState state,
        bool hasConnectionSnapshot,
        bool expected)
    {
        Assert.Equal(expected, TerminalReconnectPolicy.CanReconnect(state, hasConnectionSnapshot));
    }
}
