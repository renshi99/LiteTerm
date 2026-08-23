using LiteTerm.Core.Connections;
using LiteTerm.Core.Sftp;
using LiteTerm.Infrastructure.Sftp;

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
}
