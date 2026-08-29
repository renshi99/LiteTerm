using System.Security.Cryptography;
using System.Text;
using LiteTerm.Core.Connections;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace LiteTerm.Infrastructure.Ssh;

public sealed class SshTerminalSession : ISshTerminalSession
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private SshClient? _client;
    private ShellStream? _shell;
    private bool _disposed;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    public async Task ConnectAsync(
        SshConnectionOptions options,
        Func<HostKeyInfo, bool> hostKeyVerifier,
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options.Validate();
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is ConnectionState.Connecting or ConnectionState.Connected)
            {
                return;
            }

            SetState(ConnectionState.Connecting);
            DisposeConnection();

            var connectionInfo = SshConnectionInfoFactory.Create(options);
            var client = new SshClient(connectionInfo)
            {
                KeepAliveInterval = options.KeepAliveInterval
            };

            client.ErrorOccurred += OnClientErrorOccurred;

            client.HostKeyReceived += (_, eventArgs) =>
            {
                var fingerprint = Convert.ToBase64String(SHA256.HashData(eventArgs.HostKey));
                eventArgs.CanTrust = hostKeyVerifier(new HostKeyInfo(
                    eventArgs.HostKeyName,
                    $"SHA256:{fingerprint.TrimEnd('=')}"));
            };

            _client = client;
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

            var shell = client.CreateShellStream(
                "xterm-256color",
                (uint)Math.Max(columns, 1),
                (uint)Math.Max(rows, 1),
                0,
                0,
                16 * 1024);
            shell.DataReceived += OnShellDataReceived;
            _shell = shell;

            SetState(ConnectionState.Connected);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            DisposeConnection();
            SetState(ConnectionState.Disconnected);
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            DisposeConnection();
            SetState(ConnectionState.Failed);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask SendAsync(string data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var shell = _shell;
        if (State != ConnectionState.Connected || shell is null)
        {
            return;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            await shell.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            await shell.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await TransitionToFailedAsync().ConfigureAwait(false);
            throw;
        }
    }

    public void Resize(int columns, int rows)
    {
        if (State != ConnectionState.Connected || _shell is null || columns <= 0 || rows <= 0)
        {
            return;
        }

        try
        {
            _shell.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
        }
        catch
        {
            _ = TransitionToFailedAsync();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || State == ConnectionState.Disconnected)
        {
            return;
        }

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(ConnectionState.Disconnecting);
            await Task.Run(DisposeConnection, CancellationToken.None).ConfigureAwait(false);
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisconnectAsync().ConfigureAwait(false);
        _disposed = true;
        _lifecycleLock.Dispose();
    }

    private void OnShellDataReceived(object? sender, ShellDataEventArgs eventArgs)
    {
        OutputReceived?.Invoke(this, new TerminalOutputEventArgs(eventArgs.Data));
    }

    private void OnClientErrorOccurred(object? sender, ExceptionEventArgs eventArgs)
    {
        _ = TransitionToFailedAsync();
    }

    private async Task TransitionToFailedAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (State != ConnectionState.Connected)
            {
                return;
            }

            DisposeConnection();
            SetState(ConnectionState.Failed);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void SetState(ConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void DisposeConnection()
    {
        var shell = _shell;
        _shell = null;
        if (shell is not null)
        {
            shell.DataReceived -= OnShellDataReceived;
            try
            {
                shell.Dispose();
            }
            catch
            {
                // A failed connection can leave the stream partially closed; continue releasing its client.
            }
        }

        var client = _client;
        _client = null;
        if (client is not null)
        {
            client.ErrorOccurred -= OnClientErrorOccurred;

            try
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }
            }
            catch
            {
                // The transport is already unusable; disposing still releases managed resources.
            }
            finally
            {
                client.Dispose();
            }
        }
    }
}
