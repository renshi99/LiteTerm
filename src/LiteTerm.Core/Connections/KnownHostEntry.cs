namespace LiteTerm.Core.Connections;

/// <summary>
/// 已确认的 SSH 主机身份记录。
/// </summary>
public sealed record KnownHostEntry(
    string Host,
    int Port,
    string Algorithm,
    string Sha256Fingerprint);
