namespace LiteTerm.Core.Servers;

/// <summary>
/// 服务器认证的敏感字段，只能通过受保护的存储读取和写入。
/// </summary>
public sealed record ServerCredential(
    Guid ServerId,
    string? Password,
    string? PrivateKeyPassphrase)
{
    public void Validate()
    {
        if (ServerId == Guid.Empty)
        {
            throw new ArgumentException("服务器标识不能为空。", nameof(ServerId));
        }
    }
}
