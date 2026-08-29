using LiteTerm.Core.Connections;

namespace LiteTerm.Core.Terminal;

public static class TerminalTabTitle
{
    public static string Format(string displayName, ConnectionState state, bool hasConnectionHistory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var suffix = state switch
        {
            ConnectionState.Connecting => "连接中",
            ConnectionState.Connected => "已连接",
            ConnectionState.Disconnecting => "断开中",
            ConnectionState.Failed => "失败",
            ConnectionState.Disconnected when hasConnectionHistory => "已断开",
            _ => null
        };
        return suffix is null ? displayName : $"{displayName} · {suffix}";
    }
}
