namespace LiteTerm.Core.Sftp;

/// <summary>
/// 表示传输目标已存在且当前策略不允许覆盖。
/// </summary>
public sealed class SftpTransferConflictException : SftpPathConflictException
{
    public SftpTransferConflictException(string path)
        : base(path)
    {
    }
}
