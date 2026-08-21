using System.Security.Cryptography;
using LiteTerm.Core.Security;

namespace LiteTerm.Infrastructure.Security;

/// <summary>
/// 使用当前 Windows 用户的 DPAPI 范围保护本地敏感数据。
/// </summary>
public sealed class WindowsDpapiSecretProtector : ISecretProtector
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        EnsureWindows();
        return ProtectedData.Protect(plaintext.ToArray(), optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        EnsureWindows();
        return ProtectedData.Unprotect(protectedData.ToArray(), optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI 仅支持在 Windows 上使用。");
        }
    }
}
