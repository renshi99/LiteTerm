using System.Text;
using LiteTerm.Core.Connections;
using LiteTerm.Infrastructure.Ssh;
using Xunit.Sdk;

namespace LiteTerm.Tests;

[Trait("Category", "Integration")]
public sealed class SshTerminalSessionIntegrationTests
{
    [Fact]
    public async Task ConnectAsync_WithConfiguredTestServer_ExchangesUtf8TerminalData()
    {
        var host = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_HOST");
        var username = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_USERNAME");
        var password = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_PASSWORD");
        if (string.IsNullOrWhiteSpace(host)
            || string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(password))
        {
            throw SkipException.ForSkip("需要设置 LITETERM_TEST_SSH_HOST、LITETERM_TEST_SSH_USERNAME 和 LITETERM_TEST_SSH_PASSWORD 才能运行 SSH 集成测试。");
        }

        var marker = $"LITETERM-{Guid.NewGuid():N}";
        var outputReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        HostKeyInfo? receivedHostKey = null;

        await using var session = new SshTerminalSession();
        session.OutputReceived += (_, eventArgs) =>
        {
            var output = Encoding.UTF8.GetString(eventArgs.Data.Span);
            if (output.Contains(marker, StringComparison.Ordinal))
            {
                outputReceived.TrySetResult(output);
            }
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await session.ConnectAsync(
            new SshConnectionOptions
            {
                Host = host,
                Username = username,
                Password = password
            },
            hostKey =>
            {
                receivedHostKey = hostKey;
                return true;
            },
            columns: 80,
            rows: 24,
            cancellation.Token);

        Assert.Equal(ConnectionState.Connected, session.State);
        Assert.NotNull(receivedHostKey);
        Assert.StartsWith("SHA256:", receivedHostKey.Sha256Fingerprint, StringComparison.Ordinal);

        await session.SendAsync($"printf '{marker} 中文\\n'\n", cancellation.Token);

        var output = await outputReceived.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellation.Token);
        Assert.Contains("中文", output, StringComparison.Ordinal);

        await session.DisconnectAsync(cancellation.Token);
        Assert.Equal(ConnectionState.Disconnected, session.State);
    }
}
