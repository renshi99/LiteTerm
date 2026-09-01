namespace LiteTerm.Core.Connections;

/// <summary>
/// 表示不会包含连接参数、路径或凭据的稳定连接失败分类。
/// </summary>
public sealed record ConnectionFailure(ConnectionFailureKind Kind)
{
    public string Code => Kind switch
    {
        ConnectionFailureKind.Timeout => "connection_timeout",
        ConnectionFailureKind.AuthenticationRejected => "authentication_rejected",
        ConnectionFailureKind.HostKeyRejected => "host_key_rejected",
        ConnectionFailureKind.NetworkUnavailable => "network_unavailable",
        ConnectionFailureKind.ConnectionLost => "connection_lost",
        ConnectionFailureKind.PermissionDenied => "permission_denied",
        ConnectionFailureKind.LocalIo => "local_io",
        ConnectionFailureKind.RemoteOperation => "remote_operation",
        _ => "unknown"
    };

    public string UserMessage => Kind switch
    {
        ConnectionFailureKind.Timeout => "连接超时，请检查地址、端口和网络后重试。",
        ConnectionFailureKind.AuthenticationRejected => "身份认证失败，请检查用户名和认证信息后重试。",
        ConnectionFailureKind.HostKeyRejected => "服务器身份未获信任，连接已阻止。请核对主机指纹。",
        ConnectionFailureKind.NetworkUnavailable => "无法访问服务器，请检查网络、地址和端口后重试。",
        ConnectionFailureKind.ConnectionLost => "与服务器的连接已中断，可在网络恢复后重连。",
        ConnectionFailureKind.PermissionDenied => "服务器拒绝了此操作，请检查远程权限。",
        ConnectionFailureKind.LocalIo => "本地文件访问失败，请检查路径和访问权限。",
        ConnectionFailureKind.RemoteOperation => "服务器未能完成请求，请稍后重试。",
        _ => "连接或远程操作失败，请重试；如问题持续，请查看诊断日志。"
    };

    public bool CanRetry => Kind is not ConnectionFailureKind.AuthenticationRejected
        and not ConnectionFailureKind.HostKeyRejected
        and not ConnectionFailureKind.PermissionDenied;
}

public enum ConnectionFailureKind
{
    Unknown,
    Timeout,
    AuthenticationRejected,
    HostKeyRejected,
    NetworkUnavailable,
    ConnectionLost,
    PermissionDenied,
    LocalIo,
    RemoteOperation
}
