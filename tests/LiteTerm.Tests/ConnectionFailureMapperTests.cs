using System.Net.Sockets;
using LiteTerm.Core.Connections;
using LiteTerm.Infrastructure.Connections;
using Renci.SshNet.Common;

namespace LiteTerm.Tests;

public sealed class ConnectionFailureMapperTests
{
    [Theory]
    [MemberData(nameof(FailureCases))]
    public void Map_ReturnsExpectedKind(
        Exception exception,
        ConnectionOperation operation,
        ConnectionFailureKind expectedKind)
    {
        var failure = ConnectionFailureMapper.Map(exception, operation);

        Assert.Equal(expectedKind, failure.Kind);
    }

    [Fact]
    public void Map_WhenHostKeyWasRejected_ReturnsHostKeyRejected()
    {
        var failure = ConnectionFailureMapper.Map(
            new SshConnectionException("connection closed"),
            ConnectionOperation.Connect,
            hostKeyRejected: true);

        Assert.Equal(ConnectionFailureKind.HostKeyRejected, failure.Kind);
    }

    [Fact]
    public void Map_UsesRecognizedInnerException()
    {
        var exception = new InvalidOperationException(
            "outer",
            new SshAuthenticationException("authentication failed"));

        var failure = ConnectionFailureMapper.Map(exception, ConnectionOperation.Connect);

        Assert.Equal(ConnectionFailureKind.AuthenticationRejected, failure.Kind);
    }

    public static TheoryData<Exception, ConnectionOperation, ConnectionFailureKind> FailureCases => new()
    {
        { new SshAuthenticationException("authentication failed"), ConnectionOperation.Connect, ConnectionFailureKind.AuthenticationRejected },
        { new SshOperationTimeoutException("timed out"), ConnectionOperation.Connect, ConnectionFailureKind.Timeout },
        { new TimeoutException("timed out"), ConnectionOperation.Transport, ConnectionFailureKind.Timeout },
        { new SocketException((int)SocketError.NetworkUnreachable), ConnectionOperation.Connect, ConnectionFailureKind.NetworkUnavailable },
        { new SocketException((int)SocketError.ConnectionReset), ConnectionOperation.Transport, ConnectionFailureKind.ConnectionLost },
        { new SftpPermissionDeniedException("permission denied"), ConnectionOperation.Transport, ConnectionFailureKind.PermissionDenied }
    };
}
