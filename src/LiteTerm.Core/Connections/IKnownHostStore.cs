namespace LiteTerm.Core.Connections;

/// <summary>
/// 提供已确认 SSH 主机身份的查询与保存。
/// </summary>
public interface IKnownHostStore
{
    KnownHostVerificationResult Verify(string host, int port, HostKeyInfo hostKey);

    void Trust(string host, int port, HostKeyInfo hostKey);
}
