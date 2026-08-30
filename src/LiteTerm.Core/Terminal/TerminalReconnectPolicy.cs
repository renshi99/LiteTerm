using LiteTerm.Core.Connections;

namespace LiteTerm.Core.Terminal;

/// <summary>
/// 统一判断终端标签是否可以使用上次连接快照执行手动重连。
/// </summary>
public static class TerminalReconnectPolicy
{
    public static bool CanReconnect(ConnectionState state, bool hasConnectionSnapshot) =>
        hasConnectionSnapshot && state is ConnectionState.Disconnected or ConnectionState.Failed;
}
