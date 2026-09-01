namespace LiteTerm.Core.Connections;

public interface ISshTerminalSession : IAsyncDisposable
{
    ConnectionState State { get; }

    ConnectionFailure? LastFailure { get; }

    event EventHandler<ConnectionState>? StateChanged;
    event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    Task ConnectAsync(
        SshConnectionOptions options,
        Func<HostKeyInfo, bool> hostKeyVerifier,
        int columns,
        int rows,
        CancellationToken cancellationToken = default);

    ValueTask SendAsync(string data, CancellationToken cancellationToken = default);
    void Resize(int columns, int rows);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
