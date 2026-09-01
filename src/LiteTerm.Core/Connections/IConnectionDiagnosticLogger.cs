namespace LiteTerm.Core.Connections;

/// <summary>
/// 写入不含主机、用户名、路径和凭据的连接诊断事件。
/// </summary>
public interface IConnectionDiagnosticLogger
{
    ValueTask WriteAsync(
        ConnectionDiagnosticEntry entry,
        CancellationToken cancellationToken = default);
}

public sealed record ConnectionDiagnosticEntry(
    DateTimeOffset OccurredAt,
    ConnectionProtocol Protocol,
    ConnectionOperation Operation,
    string FailureCode,
    string ExceptionType);

public enum ConnectionProtocol
{
    Ssh,
    Sftp
}

public enum ConnectionOperation
{
    Connect,
    Transport,
    Send,
    Resize
}
