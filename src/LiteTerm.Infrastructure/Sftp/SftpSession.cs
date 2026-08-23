using System.Security.Cryptography;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Sftp;
using LiteTerm.Infrastructure.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace LiteTerm.Infrastructure.Sftp;

public sealed class SftpSession : ISftpSession
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SftpClient? _client;
    private bool _disposed;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public string? WorkingDirectory { get; private set; }

    public event EventHandler<ConnectionState>? StateChanged;

    public async Task ConnectAsync(
        SshConnectionOptions options,
        Func<HostKeyInfo, bool> hostKeyVerifier,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options.Validate();
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State is ConnectionState.Connecting or ConnectionState.Connected)
            {
                return;
            }

            SetState(ConnectionState.Connecting);
            DisposeClient();

            var client = new SftpClient(SshConnectionInfoFactory.Create(options))
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
            WorkingDirectory = RemotePath.Normalize(client.WorkingDirectory);
            SetState(ConnectionState.Connected);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            DisposeClient();
            SetState(ConnectionState.Disconnected);
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            DisposeClient();
            SetState(ConnectionState.Failed);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedPath = RemotePath.Normalize(path);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _client;
            if (State != ConnectionState.Connected || client is null)
            {
                throw new InvalidOperationException("SFTP 会话尚未连接。");
            }

            var entries = new List<RemoteFileEntry>();
            await foreach (var file in client.ListDirectoryAsync(normalizedPath, cancellationToken)
                               .ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                {
                    continue;
                }

                entries.Add(MapEntry(file));
            }

            return entries
                .OrderByDescending(static entry => entry.Type == RemoteFileType.Directory)
                .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || State == ConnectionState.Disconnected)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(ConnectionState.Disconnecting);
            DisposeClient();
            SetState(ConnectionState.Disconnected);
        }
        finally
        {
            _gate.Release();
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
        _gate.Dispose();
    }

    private static RemoteFileEntry MapEntry(ISftpFile file)
    {
        var type = file.IsSymbolicLink
            ? RemoteFileType.SymbolicLink
            : file.IsDirectory
                ? RemoteFileType.Directory
                : file.IsRegularFile
                    ? RemoteFileType.File
                    : RemoteFileType.Other;

        return new RemoteFileEntry(
            file.Name,
            file.FullName,
            type,
            file.Length,
            new DateTimeOffset(DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc)),
            FormatPermissions(file));
    }

    private static string FormatPermissions(ISftpFile file)
    {
        return string.Create(9, file, static (characters, value) =>
        {
            characters[0] = value.OwnerCanRead ? 'r' : '-';
            characters[1] = value.OwnerCanWrite ? 'w' : '-';
            characters[2] = value.OwnerCanExecute ? 'x' : '-';
            characters[3] = value.GroupCanRead ? 'r' : '-';
            characters[4] = value.GroupCanWrite ? 'w' : '-';
            characters[5] = value.GroupCanExecute ? 'x' : '-';
            characters[6] = value.OthersCanRead ? 'r' : '-';
            characters[7] = value.OthersCanWrite ? 'w' : '-';
            characters[8] = value.OthersCanExecute ? 'x' : '-';
        });
    }

    private void SetState(ConnectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
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
            await _gate.WaitAsync().ConfigureAwait(false);
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

            DisposeClient();
            SetState(ConnectionState.Failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void DisposeClient()
    {
        WorkingDirectory = null;
        var client = _client;
        _client = null;
        if (client is null)
        {
            return;
        }

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
            // 连接已不可用时仍继续释放底层会话资源。
        }
        finally
        {
            client.Dispose();
        }
    }
}
