namespace LiteTerm.Core.Sftp;

public sealed record RemoteFileEntry(
    string Name,
    string FullPath,
    RemoteFileType Type,
    long Size,
    DateTimeOffset LastWriteTime,
    string Permissions);
