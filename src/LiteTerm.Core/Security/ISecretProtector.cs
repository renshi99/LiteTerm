namespace LiteTerm.Core.Security;

/// <summary>
/// 保护仅应短暂驻留内存中的敏感字节，以便安全地写入本地存储。
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    byte[] Unprotect(ReadOnlySpan<byte> protectedData);
}
