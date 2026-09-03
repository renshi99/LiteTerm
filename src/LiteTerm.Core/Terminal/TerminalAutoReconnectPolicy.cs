using LiteTerm.Core.Connections;

namespace LiteTerm.Core.Terminal;

/// <summary>
/// Defines the bounded conditions and delays for an explicitly enabled terminal auto-reconnect loop.
/// </summary>
public static class TerminalAutoReconnectPolicy
{
    public const int MaximumAttempts = 3;

    public static bool CanSchedule(
        bool enabled,
        ConnectionState state,
        bool hasConnectionSnapshot,
        bool hasSuccessfulConnection,
        ConnectionFailure? failure,
        int completedAttempts) =>
        enabled
        && state == ConnectionState.Failed
        && hasConnectionSnapshot
        && hasSuccessfulConnection
        && failure?.CanRetry == true
        && completedAttempts is >= 0 and < MaximumAttempts;

    public static TimeSpan GetDelay(int nextAttempt) => nextAttempt switch
    {
        1 => TimeSpan.FromSeconds(2),
        2 => TimeSpan.FromSeconds(5),
        3 => TimeSpan.FromSeconds(10),
        _ => throw new ArgumentOutOfRangeException(nameof(nextAttempt))
    };
}
