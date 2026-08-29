namespace LiteTerm.Core.Sftp;

/// <summary>
/// 表示远程或本地目标路径已被其他项目占用。
/// </summary>
public class SftpPathConflictException : IOException
{
    public SftpPathConflictException(string path)
        : base($"目标路径已存在：{path}")
    {
        Path = path;
    }

    public string Path { get; }
}
