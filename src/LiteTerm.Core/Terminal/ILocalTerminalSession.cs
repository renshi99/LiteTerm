using LiteTerm.Core.Connections;

namespace LiteTerm.Core.Terminal;

/// <summary>
/// Represents one local pseudo-terminal process owned by a terminal tab.
/// </summary>
public interface ILocalTerminalSession : IAsyncDisposable
{
    bool IsRunning { get; }

    event EventHandler<TerminalOutputEventArgs>? OutputReceived;
    event EventHandler<LocalTerminalExitedEventArgs>? Exited;

    Task StartAsync(
        int columns,
        int rows,
        CancellationToken cancellationToken = default);

    ValueTask SendAsync(string data, CancellationToken cancellationToken = default);
    void Resize(int columns, int rows);
    Task StopAsync();
}

public sealed class LocalTerminalExitedEventArgs(int exitCode) : EventArgs
{
    public int ExitCode { get; } = exitCode;
}
