using LiteTerm.Core.Connections;

namespace LiteTerm.Tests;

public sealed class ConnectionFailureTests
{
    [Theory]
    [InlineData(ConnectionFailureKind.Timeout, "connection_timeout", true)]
    [InlineData(ConnectionFailureKind.AuthenticationRejected, "authentication_rejected", false)]
    [InlineData(ConnectionFailureKind.HostKeyRejected, "host_key_rejected", false)]
    [InlineData(ConnectionFailureKind.NetworkUnavailable, "network_unavailable", true)]
    [InlineData(ConnectionFailureKind.ConnectionLost, "connection_lost", true)]
    [InlineData(ConnectionFailureKind.PermissionDenied, "permission_denied", false)]
    [InlineData(ConnectionFailureKind.LocalIo, "local_io", true)]
    [InlineData(ConnectionFailureKind.RemoteOperation, "remote_operation", true)]
    [InlineData(ConnectionFailureKind.Unknown, "unknown", true)]
    public void FailureMetadata_IsStableAndSafe(
        ConnectionFailureKind kind,
        string expectedCode,
        bool expectedCanRetry)
    {
        var failure = new ConnectionFailure(kind);

        Assert.Equal(expectedCode, failure.Code);
        Assert.Equal(expectedCanRetry, failure.CanRetry);
        Assert.NotEmpty(failure.UserMessage);
        Assert.DoesNotContain("Exception", failure.UserMessage, StringComparison.OrdinalIgnoreCase);
    }
}
