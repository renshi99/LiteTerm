using System.Text;
using LiteTerm.Core.Connections;
using LiteTerm.Infrastructure.Ssh;

namespace LiteTerm.Tests;

[Trait("Category", "Integration")]
public sealed class SshTerminalSessionIntegrationTests
{
    [SshIntegrationFact]
    public async Task ConnectAsync_WithConfiguredTestServer_ExchangesUtf8TerminalData()
    {
        var host = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_HOST");
        var username = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_USERNAME");
        var password = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_PASSWORD");

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
                Host = host!,
                Username = username!,
                Password = password!
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

    [SshStressIntegrationFact]
    public async Task ConnectAndDispose_Repeatedly_ExchangesDataEveryTime()
    {
        var host = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_HOST")!;
        var username = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_USERNAME")!;
        var password = Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_PASSWORD")!;
        var connectionCount = int.Parse(
            Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_STRESS_CONNECTIONS")!);
        string? expectedFingerprint = null;

        for (var iteration = 1; iteration <= connectionCount; iteration++)
        {
            var marker = $"LITETERM-STRESS-{iteration}-{Guid.NewGuid():N}";
            var receivedText = new StringBuilder();
            var outputReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            HostKeyInfo? receivedHostKey = null;

            await using var session = new SshTerminalSession();
            session.OutputReceived += (_, eventArgs) =>
            {
                lock (receivedText)
                {
                    receivedText.Append(Encoding.UTF8.GetString(eventArgs.Data.Span));
                    if (receivedText.ToString().Contains(marker, StringComparison.Ordinal))
                    {
                        outputReceived.TrySetResult();
                    }
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
            expectedFingerprint ??= receivedHostKey.Sha256Fingerprint;
            Assert.Equal(expectedFingerprint, receivedHostKey.Sha256Fingerprint);

            await session.SendAsync($"printf '{marker}\\n'\n", cancellation.Token);
            await outputReceived.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellation.Token);
            await session.DisconnectAsync(cancellation.Token);
            Assert.Equal(ConnectionState.Disconnected, session.State);
        }
    }
}

public class SshIntegrationFactAttribute : FactAttribute
{
    public SshIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_HOST"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_USERNAME"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_PASSWORD")))
        {
            Skip = "需要设置 SSH 集成测试环境变量后才能运行。";
        }
    }
}

public sealed class LargeSftpIntegrationFactAttribute : SshIntegrationFactAttribute
{
    public LargeSftpIntegrationFactAttribute()
    {
        if (Skip is null &&
            (!long.TryParse(
                Environment.GetEnvironmentVariable("LITETERM_TEST_SFTP_LARGE_FILE_BYTES"),
                out var fileSize) ||
             fileSize < 1))
        {
            Skip = "需要设置有效的 LITETERM_TEST_SFTP_LARGE_FILE_BYTES 后才能运行大文件测试。";
        }
    }
}

public sealed class SshStressIntegrationFactAttribute : SshIntegrationFactAttribute
{
    public SshStressIntegrationFactAttribute()
    {
        if (Skip is null &&
            (!int.TryParse(
                Environment.GetEnvironmentVariable("LITETERM_TEST_SSH_STRESS_CONNECTIONS"),
                out var connectionCount) ||
             connectionCount is < 1 or > 100))
        {
            Skip = "需要将 LITETERM_TEST_SSH_STRESS_CONNECTIONS 设置为 1 到 100 后才能运行压力测试。";
        }
    }
}
