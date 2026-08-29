using LiteTerm.Core.Connections;
using LiteTerm.Core.Terminal;

namespace LiteTerm.Tests;

public sealed class TerminalTabTitleTests
{
    [Theory]
    [InlineData(ConnectionState.Connecting, false, "ren@server · 连接中")]
    [InlineData(ConnectionState.Connected, true, "ren@server · 已连接")]
    [InlineData(ConnectionState.Disconnecting, true, "ren@server · 断开中")]
    [InlineData(ConnectionState.Failed, true, "ren@server · 失败")]
    [InlineData(ConnectionState.Disconnected, true, "ren@server · 已断开")]
    [InlineData(ConnectionState.Disconnected, false, "ren@server")]
    public void Format_ReflectsSessionState(
        ConnectionState state,
        bool hasConnectionHistory,
        string expected)
    {
        Assert.Equal(expected, TerminalTabTitle.Format("ren@server", state, hasConnectionHistory));
    }

    [Fact]
    public void Format_RejectsBlankDisplayName()
    {
        Assert.Throws<ArgumentException>(() =>
            TerminalTabTitle.Format(" ", ConnectionState.Disconnected, false));
    }
}
