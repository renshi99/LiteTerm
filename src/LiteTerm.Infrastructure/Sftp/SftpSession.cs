using System.Security.Cryptography;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Sftp;
using LiteTerm.Infrastructure.Connections;
using LiteTerm.Infrastructure.Diagnostics;
using LiteTerm.Infrastructure.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace LiteTerm.Infrastructure.Sftp;

public sealed class SftpSession : ISftpSession
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IConnectionDiagnosticLogger _diagnosticLogger;
    private SftpClient? _client;
    private bool _disposed;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public ConnectionFailure? LastFailure { get; private set; }

    public string? WorkingDirectory { get; private set; }

    public event EventHandler<ConnectionState>? StateChanged;

    public SftpSession(IConnectionDiagnosticLogger? diagnosticLogger = null)
    {
        _diagnosticLogger = diagnosticLogger ?? NullConnectionDiagnosticLogger.Instance;
    }

    public async Task ConnectAsync(
        SshConnectionOptions options,
        Func<HostKeyInfo, bool> hostKeyVerifier,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options.Validate();
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var hostKeyRejected = false;
        try
        {
            if (State is ConnectionState.Connecting or ConnectionState.Connected)
            {
                return;
            }

            LastFailure = null;
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
                hostKeyRejected = !eventArgs.CanTrust;
            };

            _client = client;
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            WorkingDirectory = RemotePath.Normalize(client.WorkingDirectory);
            SetState(ConnectionState.Connected);
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            DisposeClient();
            LastFailure = null;
            SetState(ConnectionState.Disconnected);
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception exception)
        {
            DisposeClient();
            await SetFailureAsync(
                exception,
                ConnectionOperation.Connect,
                hostKeyRejected).ConfigureAwait(false);
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

    public async Task UploadFileAsync(
        string localPath,
        string remotePath,
        SftpTransferConflictPolicy conflictPolicy,
        IProgress<SftpTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ValidateConflictPolicy(conflictPolicy);
        var normalizedRemotePath = RemotePath.Normalize(remotePath);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            if (await client.ExistsAsync(normalizedRemotePath, cancellationToken).ConfigureAwait(false))
            {
                if (conflictPolicy == SftpTransferConflictPolicy.Fail)
                {
                    throw new SftpTransferConflictException(normalizedRemotePath);
                }

                var destinationAttributes = await client
                    .GetAttributesAsync(normalizedRemotePath, cancellationToken)
                    .ConfigureAwait(false);
                EnsureRegularFileUploadTarget(destinationAttributes, normalizedRemotePath);
            }

            await using var input = new FileStream(
                localPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var totalBytes = input.Length;
            var temporaryPath = CreateRemoteTemporaryPath(normalizedRemotePath, "part");
            var committed = false;
            try
            {
                progress?.Report(CreateProgress(0, totalBytes));
                var uploadProgress = progress is null
                    ? null
                    : new ForwardingProgress<UploadFileProgressReport>(report =>
                        progress.Report(CreateProgress(report.TotalBytesUploaded, totalBytes)));

                await client.UploadFileAsync(
                        input,
                        temporaryPath,
                        canOverride: false,
                        uploadProgress,
                        cancellationToken)
                    .ConfigureAwait(false);

                await CommitRemoteUploadAsync(
                        client,
                        temporaryPath,
                        normalizedRemotePath,
                        conflictPolicy,
                        cancellationToken)
                    .ConfigureAwait(false);
                committed = true;
                progress?.Report(CreateProgress(totalBytes, totalBytes));
            }
            finally
            {
                if (!committed)
                {
                    await TryDeleteRemoteFileAsync(client, temporaryPath).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DownloadFileAsync(
        string remotePath,
        string localPath,
        SftpTransferConflictPolicy conflictPolicy,
        IProgress<SftpTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        ValidateConflictPolicy(conflictPolicy);
        var normalizedRemotePath = RemotePath.Normalize(remotePath);
        var fullLocalPath = Path.GetFullPath(localPath);

        if (conflictPolicy == SftpTransferConflictPolicy.Fail && File.Exists(fullLocalPath))
        {
            throw new SftpTransferConflictException(fullLocalPath);
        }

        var localDirectory = Path.GetDirectoryName(fullLocalPath)
            ?? throw new ArgumentException("本地目标路径必须包含有效目录。", nameof(localPath));
        var temporaryPath = Path.Combine(
            localDirectory,
            $".liteterm-{Guid.NewGuid():N}.part");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            var attributes = await client.GetAttributesAsync(normalizedRemotePath, cancellationToken)
                .ConfigureAwait(false);
            if (!attributes.IsRegularFile)
            {
                throw new IOException($"远程路径不是常规文件：{normalizedRemotePath}");
            }

            var totalBytes = checked((long)attributes.Size);
            var committed = false;
            try
            {
                await using (var output = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    progress?.Report(CreateProgress(0, totalBytes));
                    var downloadProgress = progress is null
                        ? null
                        : new ForwardingProgress<DownloadFileProgressReport>(report =>
                            progress.Report(CreateProgress(report.TotalBytesDownloaded, totalBytes)));

                    await client.DownloadFileAsync(
                            normalizedRemotePath,
                            output,
                            downloadProgress,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    File.Move(
                        temporaryPath,
                        fullLocalPath,
                        overwrite: conflictPolicy == SftpTransferConflictPolicy.Overwrite);
                }
                catch (IOException) when (
                    conflictPolicy == SftpTransferConflictPolicy.Fail && File.Exists(fullLocalPath))
                {
                    throw new SftpTransferConflictException(fullLocalPath);
                }

                committed = true;
                progress?.Report(CreateProgress(totalBytes, totalBytes));
            }
            finally
            {
                if (!committed)
                {
                    TryDeleteLocalFile(temporaryPath);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedPath = RemotePath.Normalize(path);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            if (await client.ExistsAsync(normalizedPath, cancellationToken).ConfigureAwait(false))
            {
                throw new SftpPathConflictException(normalizedPath);
            }

            await client.CreateDirectoryAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenameAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedSourcePath = RemotePath.Normalize(sourcePath);
        var normalizedDestinationPath = RemotePath.Normalize(destinationPath);
        if (normalizedSourcePath is "/" or ".")
        {
            throw new IOException("不能重命名远程根目录或当前工作目录。");
        }

        if (normalizedSourcePath == normalizedDestinationPath)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            if (normalizedSourcePath == WorkingDirectory)
            {
                throw new IOException("不能重命名远程根目录或当前工作目录。");
            }

            if (await client.ExistsAsync(normalizedDestinationPath, cancellationToken).ConfigureAwait(false))
            {
                throw new SftpPathConflictException(normalizedDestinationPath);
            }

            await client.RenameFileAsync(
                    normalizedSourcePath,
                    normalizedDestinationPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedPath = RemotePath.Normalize(path);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            var attributes = await client.GetAttributesAsync(normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (attributes.IsDirectory && !attributes.IsSymbolicLink)
            {
                throw new IOException($"远程目标是目录，不能按文件删除：{normalizedPath}");
            }

            await client.DeleteFileAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedPath = RemotePath.Normalize(path);
        if (normalizedPath is "/" or ".")
        {
            throw new IOException("不能删除远程根目录或当前工作目录。");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = GetConnectedClient();
            if (normalizedPath == WorkingDirectory)
            {
                throw new IOException("不能删除远程根目录或当前工作目录。");
            }

            var attributes = await client.GetAttributesAsync(normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            if (!attributes.IsDirectory || attributes.IsSymbolicLink)
            {
                throw new IOException($"远程目标不是目录：{normalizedPath}");
            }

            await client.DeleteDirectoryAsync(normalizedPath, cancellationToken).ConfigureAwait(false);
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
            LastFailure = null;
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

    private SftpClient GetConnectedClient()
    {
        if (State != ConnectionState.Connected || _client is null)
        {
            throw new InvalidOperationException("SFTP 会话尚未连接。");
        }

        return _client;
    }

    private static void ValidateConflictPolicy(SftpTransferConflictPolicy conflictPolicy)
    {
        if (!Enum.IsDefined(conflictPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(conflictPolicy));
        }
    }

    private static SftpTransferProgress CreateProgress(ulong bytesTransferred, long totalBytes)
    {
        var transferred = bytesTransferred > long.MaxValue
            ? long.MaxValue
            : (long)bytesTransferred;
        return CreateProgress(transferred, totalBytes);
    }

    private static SftpTransferProgress CreateProgress(long bytesTransferred, long totalBytes)
    {
        return new SftpTransferProgress(
            Math.Min(bytesTransferred, totalBytes),
            totalBytes,
            DateTimeOffset.UtcNow);
    }

    private static string CreateRemoteTemporaryPath(string remotePath, string suffix)
    {
        return RemotePath.Combine(
            RemotePath.GetParent(remotePath),
            $".liteterm-{Guid.NewGuid():N}.{suffix}");
    }

    private static async Task CommitRemoteUploadAsync(
        SftpClient client,
        string temporaryPath,
        string destinationPath,
        SftpTransferConflictPolicy conflictPolicy,
        CancellationToken cancellationToken)
    {
        var destinationExists = await client.ExistsAsync(destinationPath, cancellationToken)
            .ConfigureAwait(false);
        if (!destinationExists)
        {
            await client.RenameFileAsync(temporaryPath, destinationPath, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (conflictPolicy == SftpTransferConflictPolicy.Fail)
        {
            throw new SftpTransferConflictException(destinationPath);
        }

        var destinationAttributes = await client.GetAttributesAsync(destinationPath, cancellationToken)
            .ConfigureAwait(false);
        EnsureRegularFileUploadTarget(destinationAttributes, destinationPath);

        var backupPath = CreateRemoteTemporaryPath(destinationPath, "backup");
        await client.RenameFileAsync(destinationPath, backupPath, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await client.RenameFileAsync(temporaryPath, destinationPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            await TryRestoreRemoteBackupAsync(client, backupPath, destinationPath).ConfigureAwait(false);
            throw;
        }

        await TryDeleteRemoteFileAsync(client, backupPath).ConfigureAwait(false);
    }

    private static void EnsureRegularFileUploadTarget(
        SftpFileAttributes attributes,
        string destinationPath)
    {
        if (attributes.IsDirectory && !attributes.IsSymbolicLink)
        {
            throw new IOException($"远程目标是目录，无法使用文件覆盖：{destinationPath}");
        }

        if (!attributes.IsRegularFile)
        {
            throw new IOException($"远程目标不是常规文件，无法使用文件覆盖：{destinationPath}");
        }
    }

    private static async Task TryRestoreRemoteBackupAsync(
        SftpClient client,
        string backupPath,
        string destinationPath)
    {
        try
        {
            using var cleanupCancellation = new CancellationTokenSource(CleanupTimeout);
            if (!await client.ExistsAsync(destinationPath, cleanupCancellation.Token).ConfigureAwait(false))
            {
                await client.RenameFileAsync(backupPath, destinationPath, cleanupCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // 保留备份文件，避免在恢复异常时进一步丢失原目标内容。
        }
    }

    private static async Task TryDeleteRemoteFileAsync(SftpClient client, string path)
    {
        try
        {
            using var cleanupCancellation = new CancellationTokenSource(CleanupTimeout);
            if (await client.ExistsAsync(path, cleanupCancellation.Token).ConfigureAwait(false))
            {
                await client.DeleteFileAsync(path, cleanupCancellation.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // 清理失败不覆盖原始传输异常；残留文件使用 LiteTerm 专用前缀便于识别。
        }
    }

    private static void TryDeleteLocalFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // 清理失败不覆盖原始传输异常。
        }
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
        _ = TransitionToFailedAsync(eventArgs.Exception);
    }

    private async Task TransitionToFailedAsync(Exception exception)
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
            await SetFailureAsync(exception, ConnectionOperation.Transport).ConfigureAwait(false);
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
                ConnectionProtocol.Sftp,
                operation,
                failure.Code,
                exception.GetType().FullName ?? exception.GetType().Name)).ConfigureAwait(false);
        }
        catch
        {
            // 诊断日志不可用时不能覆盖原始连接错误或阻止会话释放。
        }
    }

    private sealed class ForwardingProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
