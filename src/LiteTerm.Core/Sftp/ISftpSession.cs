using LiteTerm.Core.Connections;

namespace LiteTerm.Core.Sftp;

/// <summary>
/// 表示与终端 SSH 会话相互独立的 SFTP 连接。
/// </summary>
public interface ISftpSession : IAsyncDisposable
{
    ConnectionState State { get; }

    string? WorkingDirectory { get; }

    event EventHandler<ConnectionState>? StateChanged;

    Task ConnectAsync(
        SshConnectionOptions options,
        Func<HostKeyInfo, bool> hostKeyVerifier,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task UploadFileAsync(
        string localPath,
        string remotePath,
        SftpTransferConflictPolicy conflictPolicy,
        IProgress<SftpTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task DownloadFileAsync(
        string remotePath,
        string localPath,
        SftpTransferConflictPolicy conflictPolicy,
        IProgress<SftpTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task CreateDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task RenameAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task DeleteFileAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task DeleteDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
