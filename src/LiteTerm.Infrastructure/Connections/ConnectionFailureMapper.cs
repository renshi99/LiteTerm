using System.Net.Sockets;
using LiteTerm.Core.Connections;
using Renci.SshNet.Common;

namespace LiteTerm.Infrastructure.Connections;

internal static class ConnectionFailureMapper
{
    public static ConnectionFailure Map(
        Exception exception,
        ConnectionOperation operation,
        bool hostKeyRejected = false)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (hostKeyRejected)
        {
            return new ConnectionFailure(ConnectionFailureKind.HostKeyRejected);
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var kind = MapSingle(current, operation);
            if (kind != ConnectionFailureKind.Unknown)
            {
                return new ConnectionFailure(kind);
            }
        }

        return new ConnectionFailure(ConnectionFailureKind.Unknown);
    }

    private static ConnectionFailureKind MapSingle(Exception exception, ConnectionOperation operation) => exception switch
    {
        SshAuthenticationException => ConnectionFailureKind.AuthenticationRejected,
        SshOperationTimeoutException or TimeoutException => ConnectionFailureKind.Timeout,
        SocketException => operation == ConnectionOperation.Connect
            ? ConnectionFailureKind.NetworkUnavailable
            : ConnectionFailureKind.ConnectionLost,
        SshConnectionException => operation == ConnectionOperation.Connect
            ? ConnectionFailureKind.NetworkUnavailable
            : ConnectionFailureKind.ConnectionLost,
        UnauthorizedAccessException or SftpPermissionDeniedException => ConnectionFailureKind.PermissionDenied,
        FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException => ConnectionFailureKind.LocalIo,
        IOException => operation == ConnectionOperation.Connect
            ? ConnectionFailureKind.LocalIo
            : ConnectionFailureKind.ConnectionLost,
        SshException => operation == ConnectionOperation.Connect
            ? ConnectionFailureKind.RemoteOperation
            : ConnectionFailureKind.ConnectionLost,
        _ => ConnectionFailureKind.Unknown
    };
}
