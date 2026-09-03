using System.Net;
using System.Net.Sockets;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Sftp;
using LiteTerm.Infrastructure.Sftp;

namespace LiteTerm.Tests;

public sealed class SftpSessionTests
{
    [Fact]
    public async Task DisposeAsync_WhenCalledConcurrentlyDuringConnect_CompletesAllCalls()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var session = new SftpSession();
        using var cancellation = new CancellationTokenSource();
        try
        {
            var connectTask = session.ConnectAsync(
                CreateOptions(listener),
                _ => true,
                cancellation.Token);
            using var acceptedClient = await listener.AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            var disposeTasks = Enumerable.Range(0, 8)
                .Select(_ => session.DisposeAsync().AsTask())
                .ToArray();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await connectTask.WaitAsync(TimeSpan.FromSeconds(5)));
            await Task.WhenAll(disposeTasks).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(ConnectionState.Disconnected, session.State);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                session.ListDirectoryAsync("/"));
        }
        finally
        {
            cancellation.Cancel();
            await session.DisposeAsync();
            listener.Stop();
        }
    }

    [Fact]
    public async Task ListDirectoryAsync_BeforeConnect_ThrowsInvalidOperationException()
    {
        await using var session = new SftpSession();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.ListDirectoryAsync("/"));

        Assert.Equal("SFTP 会话尚未连接。", exception.Message);
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledMoreThanOnce()
    {
        var session = new SftpSession();

        await session.DisposeAsync();
        await session.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.ListDirectoryAsync("/"));
    }

    [Fact]
    public async Task UploadFileAsync_BeforeConnect_ThrowsInvalidOperationException()
    {
        await using var session = new SftpSession();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.UploadFileAsync(
                "missing.txt",
                "/missing.txt",
                SftpTransferConflictPolicy.Fail));

        Assert.Equal("SFTP 会话尚未连接。", exception.Message);
    }

    [Fact]
    public async Task DownloadFileAsync_WhenLocalTargetExistsAndOverwriteDisabled_LeavesFileUnchanged()
    {
        var targetPath = Path.Combine(Path.GetTempPath(), $"LiteTerm-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(targetPath, "existing");
        try
        {
            await using var session = new SftpSession();

            var exception = await Assert.ThrowsAsync<SftpTransferConflictException>(
                () => session.DownloadFileAsync(
                    "/remote.txt",
                    targetPath,
                    SftpTransferConflictPolicy.Fail));

            Assert.Equal(targetPath, exception.Path);
            Assert.Equal("existing", await File.ReadAllTextAsync(targetPath));
        }
        finally
        {
            File.Delete(targetPath);
        }
    }

    [Fact]
    public void TransferProgress_CalculatesBoundedPercentage()
    {
        Assert.Equal(25, new SftpTransferProgress(25, 100, DateTimeOffset.UtcNow).Percentage);
        Assert.Equal(100, new SftpTransferProgress(0, 0, DateTimeOffset.UtcNow).Percentage);
        Assert.Equal(100, new SftpTransferProgress(150, 100, DateTimeOffset.UtcNow).Percentage);
    }

    [Fact]
    public async Task UploadFileAsync_WithUnknownConflictPolicy_ThrowsArgumentOutOfRangeException()
    {
        await using var session = new SftpSession();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => session.UploadFileAsync("source.txt", "/target.txt", (SftpTransferConflictPolicy)99));
    }

    [Fact]
    public async Task RemoteFileOperations_BeforeConnect_ThrowInvalidOperationException()
    {
        await using var session = new SftpSession();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.CreateDirectoryAsync("/new"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RenameAsync("/old", "/new"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DeleteFileAsync("/file"));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DeleteDirectoryAsync("/directory"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData(".")]
    public async Task DeleteDirectoryAsync_ForRootOrCurrentDirectory_RejectsBeforeConnecting(string path)
    {
        await using var session = new SftpSession();

        var exception = await Assert.ThrowsAsync<IOException>(
            () => session.DeleteDirectoryAsync(path));

        Assert.Equal("不能删除远程根目录或当前工作目录。", exception.Message);
    }

    [Theory]
    [InlineData("/")]
    [InlineData(".")]
    public async Task RenameAsync_ForRootOrCurrentDirectory_RejectsBeforeConnecting(string path)
    {
        await using var session = new SftpSession();

        var exception = await Assert.ThrowsAsync<IOException>(
            () => session.RenameAsync(path, "/renamed"));

        Assert.Equal("不能重命名远程根目录或当前工作目录。", exception.Message);
    }

    private static SshConnectionOptions CreateOptions(TcpListener listener) => new()
    {
        Host = IPAddress.Loopback.ToString(),
        Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        Username = "test-user",
        Password = "test-password",
        ConnectTimeout = TimeSpan.FromSeconds(30)
    };
}
