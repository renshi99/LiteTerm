using System.Text;
using LiteTerm.Infrastructure.Terminal;

namespace LiteTerm.Tests;

public sealed class WindowsPseudoConsoleSessionTests
{
    [Fact]
    public async Task StartAndSendAsync_RoundTripsPowerShellOutput()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        await using var session = new WindowsPseudoConsoleSession();
        var marker = $"LITETERM_LOCAL_{Guid.NewGuid():N}";
        var output = new StringBuilder();
        var markerReceived = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.OutputReceived += (_, eventArgs) =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(eventArgs.Data.Span));
                if (output.ToString().Contains(marker, StringComparison.Ordinal))
                {
                    markerReceived.TrySetResult();
                }
            }
        };

        await session.StartAsync(80, 24);
        session.Resize(100, 30);
        var markerSuffix = marker["LITETERM_".Length..];
        await session.SendAsync($"Write-Output ('LITETERM_' + '{markerSuffix}')\r");
        await markerReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(session.IsRunning);
        await session.StopAsync();
        Assert.False(session.IsRunning);
    }

    [Fact]
    public async Task DisposeAsync_WhenCalledConcurrently_StopsProcessAndCompletesAllCalls()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        var session = new WindowsPseudoConsoleSession();
        await session.StartAsync(80, 24);

        var disposeTasks = Enumerable.Range(0, 8)
            .Select(_ => session.DisposeAsync().AsTask())
            .ToArray();
        await Task.WhenAll(disposeTasks).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(session.IsRunning);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.StartAsync(80, 24));
    }

    [Fact]
    public async Task StartAsync_AfterShellExit_StartsANewLocalTerminal()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }

        await using var session = new WindowsPseudoConsoleSession();
        var exited = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Exited += (_, _) => exited.TrySetResult();

        await session.StartAsync(80, 24);
        await session.SendAsync("exit\r");
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(session.IsRunning);

        await session.StartAsync(80, 24);
        Assert.True(session.IsRunning);
    }

    [Theory]
    [InlineData(0, 24)]
    [InlineData(80, 0)]
    [InlineData(32768, 24)]
    [InlineData(80, 32768)]
    public async Task StartAsync_WithInvalidSize_ThrowsArgumentOutOfRangeException(
        int columns,
        int rows)
    {
        await using var session = new WindowsPseudoConsoleSession();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.StartAsync(columns, rows));
    }
}
