namespace LiteTerm.Core.Sftp;

/// <summary>
/// 指定传输目标已存在时的处理方式。
/// </summary>
public enum SftpTransferConflictPolicy
{
    Fail,
    Overwrite
}
