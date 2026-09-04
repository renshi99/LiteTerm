using System.Security.Cryptography;
using System.Text;
using LiteTerm.Core.Connections;
using LiteTerm.Infrastructure.Connections;
using LiteTerm.Infrastructure.Diagnostics;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace LiteTerm.Infrastructure.Ssh;

public sealed class SshTerminalSession : ISshTerminalSession
{
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly IConnectionDiagnosticLogger _diagnosticLogger;
    private readonly TaskCompletionSource _disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private SshClient? _client;
    private ShellStream? _shell;
    private int _disposeStarted;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public ConnectionFailure? LastFailure { get; private set; }

    public event EventHandler<ConnectionState>? StateChanged;
    public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

    public SshTerminalSession(IConnectionDiagnosticLogger? diagnosticLogger = null)
    {
        _diagnosticLogger = diagnosticLogger ?? NullConnectionDiagnosticLogger.Instance;
    }

    public async Task ConnectAsync(
        SshConnectionOptions options,
        Func<HostKeyInfo, bool> hostKeyVerifier,
        int columns,
        int rows,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposingOrDisposed, this);
        options.Validate();
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var hostKeyRejected = false;
        try
        {
            if (State is ConnectionState.Connecting or ConnectionState.Connected)
            {
                return;
            }

            LastFailure = null;
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
                hostKeyRejected = !eventArgs.CanTrust;
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
            shell.ErrorOccurred += OnShellErrorOccurred;
            shell.Closed += OnShellClosed;
            _shell = shell;

            SetState(ConnectionState.Connected);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            DisposeConnection();
            LastFailure = null;
            SetState(ConnectionState.Disconnected);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
        {
            DisposeConnection();
            await SetFailureAsync(
                exception,
                ConnectionOperation.Connect,
                hostKeyRejected).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask SendAsync(string data, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposingOrDisposed, this);
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
        catch (Exception exception)
        {
            await TransitionToFailedAsync(exception, ConnectionOperation.Send).ConfigureAwait(false);
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
        catch (Exception exception)
        {
            _ = TransitionToFailedAsync(exception, ConnectionOperation.Resize);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposingOrDisposed || State == ConnectionState.Disconnected)
        {
            return;
        }

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(ConnectionState.Disconnecting);
            await Task.Run(DisposeConnection, CancellationToken.None).ConfigureAwait(false);
            LastFailure = null;
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeStarted, 1, 0) == 0)
        {
            _ = DisposeCoreAsync();
        }

        return new ValueTask(_disposeCompletion.Task);
    }

    private void OnShellDataReceived(object? sender, ShellDataEventArgs eventArgs)
    {
        OutputReceived?.Invoke(this, new TerminalOutputEventArgs(eventArgs.Data));
    }

    private void OnShellErrorOccurred(object? sender, ExceptionEventArgs eventArgs)
    {
        _ = TransitionToFailedAsync(eventArgs.Exception, ConnectionOperation.Transport);
    }

    private void OnShellClosed(object? sender, EventArgs eventArgs)
    {
        _ = TransitionToDisconnectedAsync();
    }

    private void OnClientErrorOccurred(object? sender, ExceptionEventArgs eventArgs)
    {
        _ = TransitionToFailedAsync(eventArgs.Exception, ConnectionOperation.Transport);
    }

    private async Task TransitionToFailedAsync(Exception exception, ConnectionOperation operation)
    {
        if (IsDisposingOrDisposed)
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
            await SetFailureAsync(exception, operation).ConfigureAwait(false);
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

    private async Task TransitionToDisconnectedAsync()
    {
        if (IsDisposingOrDisposed)
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

            if (_client?.IsConnected == true)
            {
                DisposeConnection();
                LastFailure = null;
                SetState(ConnectionState.Disconnected);
            }
            else
            {
                DisposeConnection();
                await SetFailureAsync(
                    new SshConnectionException("SSH transport closed."),
                    ConnectionOperation.Transport).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private bool IsDisposingOrDisposed => Volatile.Read(ref _disposeStarted) != 0;

    private async Task DisposeCoreAsync()
    {
        Exception? failure = null;
        try
        {
            await _lifecycleLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (State != ConnectionState.Disconnected)
                {
                    SetState(ConnectionState.Disconnecting);
                    await Task.Run(DisposeConnection, CancellationToken.None).ConfigureAwait(false);
                    LastFailure = null;
                    SetState(ConnectionState.Disconnected);
                }
            }
            finally
            {
                _lifecycleLock.Release();
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                _lifecycleLock.Dispose();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            if (failure is null)
            {
                _disposeCompletion.TrySetResult();
            }
            else
            {
                _disposeCompletion.TrySetException(failure);
            }
        }
    }

    private async ValueTask SetFailureAsync(
        Exception exception,
        ConnectionOperation operation,
        bool hostKeyRejected = false)
    {
        var failure = ConnectionFailureMapper.Map(exception, operation, hostKeyRejected);
        LastFailure = failure;
        SetState(ConnectionState.Failed);

        try
        {
            await _diagnosticLogger.WriteAsync(new ConnectionDiagnosticEntry(
                DateTimeOffset.UtcNow,
                ConnectionProtocol.Ssh,
                operation,
                failure.Code,
                exception.GetType().FullName ?? exception.GetType().Name)).ConfigureAwait(false);
        }
        catch
        {
            // 诊断日志不可用时不能覆盖原始连接错误或阻止会话释放。
        }
    }

    private void DisposeConnection()
    {
        var shell = _shell;
        _shell = null;
        if (shell is not null)
        {
            shell.DataReceived -= OnShellDataReceived;
            shell.ErrorOccurred -= OnShellErrorOccurred;
            shell.Closed -= OnShellClosed;
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
