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

            var connectionInfo = CreateConnectionInfo(options);
            var client = new SshClient(connectionInfo)
            {
                KeepAliveInterval = options.KeepAliveInterval
            };

            client.HostKeyReceived += (_, eventArgs) =>
            {
                var fingerprint = Convert.ToBase64String(SHA256.HashData(eventArgs.HostKey));
                eventArgs.CanTrust = hostKeyVerifier(new HostKeyInfo(
                    eventArgs.HostKeyName,
                    $"SHA256:{fingerprint.TrimEnd('=')}"));
            };

            _client = client;
            await Task.Run(client.Connect, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

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

        var bytes = Encoding.UTF8.GetBytes(data);
        await shell.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
        await shell.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Resize(int columns, int rows)
    {
        if (State != ConnectionState.Connected || _shell is null || columns <= 0 || rows <= 0)
        {
            return;
        }

        _shell.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
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

    private static ConnectionInfo CreateConnectionInfo(SshConnectionOptions options)
    {
        AuthenticationMethod authentication = options.AuthenticationType switch
        {
            SshAuthenticationType.Password => new PasswordAuthenticationMethod(
                options.Username,
                options.Password ?? string.Empty),
            SshAuthenticationType.PrivateKey => new PrivateKeyAuthenticationMethod(
                options.Username,
                CreatePrivateKeyFile(options)),
            _ => throw new ArgumentOutOfRangeException(nameof(options.AuthenticationType))
        };

        return new ConnectionInfo(options.Host, options.Port, options.Username, authentication)
        {
            Timeout = options.ConnectTimeout
        };
    }

    private static PrivateKeyFile CreatePrivateKeyFile(SshConnectionOptions options)
    {
        return string.IsNullOrEmpty(options.PrivateKeyPassphrase)
            ? new PrivateKeyFile(options.PrivateKeyPath!)
            : new PrivateKeyFile(options.PrivateKeyPath!, options.PrivateKeyPassphrase);
    }

    private void OnShellDataReceived(object? sender, ShellDataEventArgs eventArgs)
    {
        OutputReceived?.Invoke(this, new TerminalOutputEventArgs(eventArgs.Data));
    }

    private void SetState(ConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
    }

    private void DisposeConnection()
    {
        if (_shell is not null)
        {
            _shell.DataReceived -= OnShellDataReceived;
            _shell.Dispose();
            _shell = null;
        }

        if (_client is not null)
        {
            if (_client.IsConnected)
            {
                _client.Disconnect();
            }

            _client.Dispose();
            _client = null;
        }
    }
}
