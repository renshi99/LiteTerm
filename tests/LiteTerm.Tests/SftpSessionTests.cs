using LiteTerm.Infrastructure.Sftp;

namespace LiteTerm.Tests;

public sealed class SftpSessionTests
{
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
}
