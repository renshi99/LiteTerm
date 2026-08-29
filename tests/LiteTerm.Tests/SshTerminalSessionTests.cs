using System.Net;
using System.Net.Sockets;
using LiteTerm.Core.Connections;
using LiteTerm.Infrastructure.Ssh;

namespace LiteTerm.Tests;

public sealed class SshTerminalSessionTests
{
    [Fact]
    public async Task ConnectAsync_WhenCancelledDuringHandshake_ReturnsToDisconnectedAndCanRetry()
    {
        await using var session = new SshTerminalSession();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var cancellation = new CancellationTokenSource();
                var connectTask = session.ConnectAsync(
                    CreateOptions(listener),
                    _ => true,
                    columns: 80,
                    rows: 24,
                    cancellation.Token);

                using var acceptedClient = await listener.AcceptTcpClientAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));

                cancellation.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    await connectTask.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(ConnectionState.Disconnected, session.State);
            }
            finally
            {
                listener.Stop();
            }
        }
    }

    [Fact]
    public async Task ConnectAsync_WhenHandshakeTimesOut_TransitionsToFailedAndDisposesCleanly()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var session = new SshTerminalSession();
            var connectTask = session.ConnectAsync(
                CreateOptions(listener) with { ConnectTimeout = TimeSpan.FromMilliseconds(500) },
                _ => true,
                columns: 80,
                rows: 24);

            using var acceptedClient = await listener.AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));

            var exception = await Record.ExceptionAsync(async () =>
                await connectTask.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.NotNull(exception);
            Assert.IsNotType<OperationCanceledException>(exception);
            Assert.Equal(ConnectionState.Failed, session.State);

            await session.DisposeAsync();
            Assert.Equal(ConnectionState.Disconnected, session.State);
        }
        finally
        {
            listener.Stop();
        }
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
