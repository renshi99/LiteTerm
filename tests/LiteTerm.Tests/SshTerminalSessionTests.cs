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
                Assert.Null(session.LastFailure);
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
            var diagnosticLogger = new RecordingDiagnosticLogger();
            await using var session = new SshTerminalSession(diagnosticLogger);
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
            Assert.Equal(ConnectionFailureKind.Timeout, session.LastFailure?.Kind);
            var entry = Assert.Single(diagnosticLogger.Entries);
            Assert.Equal(ConnectionProtocol.Ssh, entry.Protocol);
            Assert.Equal(ConnectionOperation.Connect, entry.Operation);
            Assert.Equal("connection_timeout", entry.FailureCode);

            await session.DisposeAsync();
            Assert.Equal(ConnectionState.Disconnected, session.State);
            Assert.Null(session.LastFailure);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectAsync_WhenFailedEventIsRaised_LastFailureIsAlreadyAvailable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var session = new SshTerminalSession();
            ConnectionFailure? failureObservedByHandler = null;
            session.StateChanged += (_, state) =>
            {
                if (state == ConnectionState.Failed)
                {
                    failureObservedByHandler = session.LastFailure;
                }
            };

            var connectTask = session.ConnectAsync(
                CreateOptions(listener) with { ConnectTimeout = TimeSpan.FromMilliseconds(500) },
                _ => true,
                columns: 80,
                rows: 24);

            using var acceptedClient = await listener.AcceptTcpClientAsync()
                .WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await connectTask.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.NotNull(failureObservedByHandler);
            Assert.Equal(ConnectionFailureKind.Timeout, failureObservedByHandler.Kind);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ConnectAsync_WhenDiagnosticLoggerThrows_PreservesOriginalFailure()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await using var session = new SshTerminalSession(new ThrowingDiagnosticLogger());
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
            Assert.DoesNotContain("diagnostic logger failed", exception.ToString(), StringComparison.Ordinal);
            Assert.Equal(ConnectionState.Failed, session.State);
            Assert.Equal(ConnectionFailureKind.Timeout, session.LastFailure?.Kind);
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

    private sealed class RecordingDiagnosticLogger : IConnectionDiagnosticLogger
    {
        public List<ConnectionDiagnosticEntry> Entries { get; } = [];

        public ValueTask WriteAsync(
            ConnectionDiagnosticEntry entry,
            CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingDiagnosticLogger : IConnectionDiagnosticLogger
    {
        public ValueTask WriteAsync(
            ConnectionDiagnosticEntry entry,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("diagnostic logger failed");
    }
}
