using System.Diagnostics;
using System.Text;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Sftp;
using LiteTerm.Infrastructure.Sftp;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace LiteTerm.Tests;

[Trait("Category", "Integration")]
public sealed class SftpSessionIntegrationTests
{
    [SshIntegrationFact]
    public async Task ConnectAndListDirectory_WithConfiguredTestServer_ReturnsWorkingDirectory()
    {
        var host = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_HOST");
        var username = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_USERNAME");
        var password = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_PASSWORD");

        await using var session = new SftpSession();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.ConnectAsync(
            new SshConnectionOptions
            {
                Host = host!,
                Username = username!,
                Password = password!
            },
            static _ => true,
            cancellation.Token);

        Assert.Equal(ConnectionState.Connected, session.State);
        Assert.NotNull(session.WorkingDirectory);

        var entries = await session.ListDirectoryAsync(session.WorkingDirectory, cancellation.Token);

        Assert.DoesNotContain(entries, static entry => entry.Name is "." or "..");
        Assert.All(entries, static entry => Assert.StartsWith("/", entry.FullPath, StringComparison.Ordinal));

        await session.DisconnectAsync(cancellation.Token);
        Assert.Equal(ConnectionState.Disconnected, session.State);
    }

    [SshIntegrationFact]
    public async Task UploadAndDownload_WithConfiguredTestServer_RoundTripsStreamingContent()
    {
        var host = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_HOST")!;
        var username = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_USERNAME")!;
        var password = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_PASSWORD")!;
        var marker = $"LiteTerm SFTP 中文 {Guid.NewGuid():N}";
        var fileName = $".liteterm-integration-{Guid.NewGuid():N}.txt";
        var localSource = Path.Combine(Path.GetTempPath(), $"LiteTerm-{Guid.NewGuid():N}.txt");
        var localDestination = Path.Combine(Path.GetTempPath(), $"LiteTerm-{Guid.NewGuid():N}.txt");
        string? remotePath = null;

        await File.WriteAllTextAsync(localSource, marker, Encoding.UTF8);
        try
        {
            await using var session = new SftpSession();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.ConnectAsync(
                new SshConnectionOptions
                {
                    Host = host,
                    Username = username,
                    Password = password
                },
                static _ => true,
                cancellation.Token);

            remotePath = RemotePath.Combine(session.WorkingDirectory!, fileName);
            var uploadSnapshots = new List<SftpTransferProgress>();
            var downloadSnapshots = new List<SftpTransferProgress>();
            var directoryException = await Assert.ThrowsAsync<IOException>(
                () => session.UploadFileAsync(
                    localSource,
                    session.WorkingDirectory!,
                    SftpTransferConflictPolicy.Overwrite,
                    cancellationToken: cancellation.Token));
            Assert.Contains("目录", directoryException.Message, StringComparison.Ordinal);

            await session.UploadFileAsync(
                localSource,
                remotePath,
                SftpTransferConflictPolicy.Fail,
                new InlineProgress<SftpTransferProgress>(uploadSnapshots.Add),
                cancellation.Token);

            await Assert.ThrowsAsync<SftpTransferConflictException>(
                () => session.UploadFileAsync(
                    localSource,
                    remotePath,
                    SftpTransferConflictPolicy.Fail,
                    cancellationToken: cancellation.Token));

            marker += " overwritten";
            await File.WriteAllTextAsync(localSource, marker, Encoding.UTF8);
            await session.UploadFileAsync(
                localSource,
                remotePath,
                SftpTransferConflictPolicy.Overwrite,
                cancellationToken: cancellation.Token);
            await session.DownloadFileAsync(
                remotePath,
                localDestination,
                SftpTransferConflictPolicy.Fail,
                new InlineProgress<SftpTransferProgress>(downloadSnapshots.Add),
                cancellation.Token);

            Assert.Equal(marker, await File.ReadAllTextAsync(localDestination, Encoding.UTF8));
            Assert.Equal(100, uploadSnapshots[^1].Percentage);
            Assert.Equal(100, downloadSnapshots[^1].Percentage);
        }
        finally
        {
            File.Delete(localSource);
            File.Delete(localDestination);
            if (remotePath is not null)
            {
                await TryDeleteRemoteTestFileAsync(host, username, password, remotePath);
            }
        }
    }

    [SshIntegrationFact]
    public async Task UploadFileAsync_WhenCancelled_LeavesSessionUsableAndDoesNotPublishTarget()
    {
        var host = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_HOST")!;
        var username = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_USERNAME")!;
        var password = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_PASSWORD")!;
        var localSource = Path.Combine(Path.GetTempPath(), $"LiteTerm-{Guid.NewGuid():N}.bin");
        var fileName = $".liteterm-integration-{Guid.NewGuid():N}.bin";
        string? remotePath = null;

        await using (var source = new FileStream(localSource, FileMode.CreateNew, FileAccess.Write))
        {
            source.SetLength(8 * 1024 * 1024);
        }

        try
        {
            await using var session = new SftpSession();
            using var operationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await session.ConnectAsync(
                new SshConnectionOptions
                {
                    Host = host,
                    Username = username,
                    Password = password
                },
                static _ => true,
                operationTimeout.Token);

            remotePath = RemotePath.Combine(session.WorkingDirectory!, fileName);
            using var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                operationTimeout.Token);
            var progress = new InlineProgress<SftpTransferProgress>(snapshot =>
            {
                if (snapshot.BytesTransferred > 0)
                {
                    transferCancellation.Cancel();
                }
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => session.UploadFileAsync(
                    localSource,
                    remotePath,
                    SftpTransferConflictPolicy.Fail,
                    progress,
                    transferCancellation.Token));

            Assert.Equal(ConnectionState.Connected, session.State);
            var entries = await session.ListDirectoryAsync(session.WorkingDirectory!, operationTimeout.Token);
            Assert.DoesNotContain(entries, entry => entry.FullPath == remotePath);
        }
        finally
        {
            File.Delete(localSource);
            if (remotePath is not null)
            {
                await TryDeleteRemoteTestFileAsync(host, username, password, remotePath);
            }
        }
    }

    [LargeSftpIntegrationFact]
    public async Task UploadAndDownload_LargeFile_StreamsWithoutLinearMemoryGrowth()
    {
        var host = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_HOST")!;
        var username = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_USERNAME")!;
        var password = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_PASSWORD")!;
        var fileSize = long.Parse(
            Environment.GetEnvironmentVariable("LITETERM_TEST_SFTP_LARGE_FILE_BYTES")!);
        var marker = Encoding.UTF8.GetBytes($"LiteTerm 大文件 {Guid.NewGuid():N}");
        var localSource = Path.Combine(Path.GetTempPath(), $"LiteTerm-{Guid.NewGuid():N}.bin");
        var localDestination = Path.Combine(Path.GetTempPath(), $"LiteTerm-{Guid.NewGuid():N}.bin");
        var fileName = $".liteterm-integration-{Guid.NewGuid():N}.bin";
        string? remotePath = null;

        await CreateSparseFileWithMarkersAsync(localSource, fileSize, marker);
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var process = Process.GetCurrentProcess();
            var baselinePrivateBytes = process.PrivateMemorySize64;
            var peakPrivateBytes = baselinePrivateBytes;
            void CaptureMemory(SftpTransferProgress _)
            {
                process.Refresh();
                peakPrivateBytes = Math.Max(peakPrivateBytes, process.PrivateMemorySize64);
            }

            await using var session = new SftpSession();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            await session.ConnectAsync(
                new SshConnectionOptions
                {
                    Host = host,
                    Username = username,
                    Password = password
                },
                static _ => true,
                cancellation.Token);

            remotePath = RemotePath.Combine(session.WorkingDirectory!, fileName);
            var uploadSnapshots = new List<SftpTransferProgress>();
            var downloadSnapshots = new List<SftpTransferProgress>();
            await session.UploadFileAsync(
                localSource,
                remotePath,
                SftpTransferConflictPolicy.Fail,
                new InlineProgress<SftpTransferProgress>(snapshot =>
                {
                    uploadSnapshots.Add(snapshot);
                    CaptureMemory(snapshot);
                }),
                cancellation.Token);
            await session.DownloadFileAsync(
                remotePath,
                localDestination,
                SftpTransferConflictPolicy.Fail,
                new InlineProgress<SftpTransferProgress>(snapshot =>
                {
                    downloadSnapshots.Add(snapshot);
                    CaptureMemory(snapshot);
                }),
                cancellation.Token);

            Assert.Equal(fileSize, new FileInfo(localDestination).Length);
            await AssertFileMarkersAsync(localDestination, fileSize, marker);
            Assert.Equal(fileSize, uploadSnapshots[^1].BytesTransferred);
            Assert.Equal(fileSize, downloadSnapshots[^1].BytesTransferred);
            Assert.True(
                peakPrivateBytes - baselinePrivateBytes < 256L * 1024 * 1024,
                $"传输期间私有内存增长过高：{peakPrivateBytes - baselinePrivateBytes:N0} 字节。");
        }
        finally
        {
            File.Delete(localSource);
            File.Delete(localDestination);
            if (remotePath is not null)
            {
                await TryDeleteRemoteTestFileAsync(host, username, password, remotePath);
            }
        }
    }

    [SshIntegrationFact]
    public async Task RemoteFileOperations_WithConfiguredTestServer_HandleConflictsTypesAndNonEmptyDirectory()
    {
        var host = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_HOST")!;
        var username = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_USERNAME")!;
        var password = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_PASSWORD")!;
        var localSource = Path.Combine(Path.GetTempPath(), $"LiteTerm-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localSource, "LiteTerm remote operation test", Encoding.UTF8);

        await using var session = new SftpSession();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string? directoryPath = null;
        string? firstFilePath = null;
        string? renamedFilePath = null;
        string? conflictFilePath = null;
        try
        {
            await session.ConnectAsync(
                new SshConnectionOptions
                {
                    Host = host,
                    Username = username,
                    Password = password
                },
                static _ => true,
                cancellation.Token);

            directoryPath = RemotePath.Combine(
                session.WorkingDirectory!,
                $".liteterm-integration-{Guid.NewGuid():N}");
            firstFilePath = RemotePath.Combine(directoryPath, "first.txt");
            renamedFilePath = RemotePath.Combine(directoryPath, "renamed.txt");
            conflictFilePath = RemotePath.Combine(directoryPath, "existing.txt");

            await session.CreateDirectoryAsync(directoryPath, cancellation.Token);
            await Assert.ThrowsAsync<SftpPathConflictException>(
                () => session.CreateDirectoryAsync(directoryPath, cancellation.Token));

            await session.UploadFileAsync(
                localSource,
                firstFilePath,
                SftpTransferConflictPolicy.Fail,
                cancellationToken: cancellation.Token);
            await session.UploadFileAsync(
                localSource,
                conflictFilePath,
                SftpTransferConflictPolicy.Fail,
                cancellationToken: cancellation.Token);

            await Assert.ThrowsAsync<IOException>(
                () => session.DeleteFileAsync(directoryPath, cancellation.Token));
            await Assert.ThrowsAsync<IOException>(
                () => session.DeleteDirectoryAsync(firstFilePath, cancellation.Token));
            await Assert.ThrowsAnyAsync<SshException>(
                () => session.DeleteDirectoryAsync(directoryPath, cancellation.Token));
            await Assert.ThrowsAsync<SftpPathConflictException>(
                () => session.RenameAsync(firstFilePath, conflictFilePath, cancellation.Token));

            await session.RenameAsync(firstFilePath, renamedFilePath, cancellation.Token);
            var entries = await session.ListDirectoryAsync(directoryPath, cancellation.Token);
            Assert.Contains(entries, entry => entry.FullPath == renamedFilePath);
            Assert.DoesNotContain(entries, entry => entry.FullPath == firstFilePath);

            await session.DeleteFileAsync(renamedFilePath, cancellation.Token);
            renamedFilePath = null;
            await session.DeleteFileAsync(conflictFilePath, cancellation.Token);
            conflictFilePath = null;
            await session.DeleteDirectoryAsync(directoryPath, cancellation.Token);
            directoryPath = null;
        }
        finally
        {
            File.Delete(localSource);
            await TryDeleteRemotePathAsync(session, renamedFilePath, isDirectory: false);
            await TryDeleteRemotePathAsync(session, firstFilePath, isDirectory: false);
            await TryDeleteRemotePathAsync(session, conflictFilePath, isDirectory: false);
            await TryDeleteRemotePathAsync(session, directoryPath, isDirectory: true);
        }
    }

    [SshIntegrationFact]
    public async Task Utf8NamesAndPermissionError_LeaveSessionUsable()
    {
        var host = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_HOST")!;
        var username = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_USERNAME")!;
        var password = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_PASSWORD")!;
        var marker = $"LiteTerm 中文路径 {Guid.NewGuid():N}";
        var localSource = Path.Combine(Path.GetTempPath(), $"LiteTerm-{Guid.NewGuid():N}.txt");
        var localDestination = Path.Combine(Path.GetTempPath(), $"LiteTerm-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(localSource, marker, Encoding.UTF8);

        await using var session = new SftpSession();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string? directoryPath = null;
        string? remoteFilePath = null;
        try
        {
            await session.ConnectAsync(
                new SshConnectionOptions
                {
                    Host = host,
                    Username = username,
                    Password = password
                },
                static _ => true,
                cancellation.Token);

            directoryPath = RemotePath.Combine(
                session.WorkingDirectory!,
                $".liteterm-中文目录-{Guid.NewGuid():N}");
            remoteFilePath = RemotePath.Combine(directoryPath, "配置 中文.txt");
            await session.CreateDirectoryAsync(directoryPath, cancellation.Token);
            await session.UploadFileAsync(
                localSource,
                remoteFilePath,
                SftpTransferConflictPolicy.Fail,
                cancellationToken: cancellation.Token);

            var entries = await session.ListDirectoryAsync(directoryPath, cancellation.Token);
            Assert.Contains(entries, entry =>
                entry.Name == "配置 中文.txt" && entry.FullPath == remoteFilePath);
            await session.DownloadFileAsync(
                remoteFilePath,
                localDestination,
                SftpTransferConflictPolicy.Fail,
                cancellationToken: cancellation.Token);
            Assert.Equal(marker, await File.ReadAllTextAsync(localDestination, Encoding.UTF8));

            var deniedPath = $"/root/.liteterm-denied-{Guid.NewGuid():N}";
            await Assert.ThrowsAnyAsync<SshException>(
                () => session.CreateDirectoryAsync(deniedPath, cancellation.Token));
            Assert.Equal(ConnectionState.Connected, session.State);
            await session.ListDirectoryAsync(session.WorkingDirectory!, cancellation.Token);
        }
        finally
        {
            File.Delete(localSource);
            File.Delete(localDestination);
            await TryDeleteRemotePathAsync(session, remoteFilePath, isDirectory: false);
            await TryDeleteRemotePathAsync(session, directoryPath, isDirectory: true);
        }
    }

    private static async Task TryDeleteRemotePathAsync(
        ISftpSession session,
        string? path,
        bool isDirectory)
    {
        if (path is null || session.State != ConnectionState.Connected)
        {
            return;
        }

        try
        {
            if (isDirectory)
            {
                await session.DeleteDirectoryAsync(path);
            }
            else
            {
                await session.DeleteFileAsync(path);
            }
        }
        catch
        {
            // 条件集成测试清理不覆盖主体断言；路径均使用专用随机前缀。
        }
    }

    private static async Task CreateSparseFileWithMarkersAsync(
        string path,
        long length,
        byte[] marker)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        stream.SetLength(length);
        foreach (var position in GetMarkerPositions(length, marker.Length))
        {
            stream.Position = position;
            await stream.WriteAsync(marker);
        }
    }

    private static async Task AssertFileMarkersAsync(
        string path,
        long length,
        byte[] marker)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        var actual = new byte[marker.Length];
        foreach (var position in GetMarkerPositions(length, marker.Length))
        {
            stream.Position = position;
            await stream.ReadExactlyAsync(actual);
            Assert.Equal(marker, actual);
        }
    }

    private static long[] GetMarkerPositions(long length, int markerLength)
    {
        if (length < markerLength)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "大文件长度不能小于内容标记。 ");
        }

        return [0, (length - markerLength) / 2, length - markerLength];
    }

    private static async Task TryDeleteRemoteTestFileAsync(
        string host,
        string username,
        string password,
        string remotePath)
    {
        try
        {
            using var client = new SftpClient(host, username, password);
            client.HostKeyReceived += static (_, eventArgs) => eventArgs.CanTrust = true;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(cancellation.Token);
            if (await client.ExistsAsync(remotePath, cancellation.Token))
            {
                await client.DeleteFileAsync(remotePath, cancellation.Token);
            }
        }
        catch
        {
            // 测试主体的断言结果优先；远端文件使用专用前缀，便于异常时识别清理。
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
