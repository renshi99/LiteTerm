namespace LiteTerm.Core.Sftp;

/// <summary>
/// 表示一次文件传输的不可变进度快照。
/// </summary>
public sealed record SftpTransferProgress(
    long BytesTransferred,
    long TotalBytes,
    DateTimeOffset Timestamp)
{
    public double Percentage => TotalBytes == 0
        ? 100
        : Math.Min(100, BytesTransferred * 100d / TotalBytes);
}
