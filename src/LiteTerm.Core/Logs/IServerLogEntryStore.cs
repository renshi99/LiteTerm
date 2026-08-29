namespace LiteTerm.Core.Logs;

public interface IServerLogEntryStore
{
    Task<IReadOnlyList<ServerLogEntry>> GetForServerAsync(
        Guid serverId,
        CancellationToken cancellationToken = default);

    Task ReplaceForServerAsync(
        Guid serverId,
        IReadOnlyList<ServerLogEntry> entries,
        CancellationToken cancellationToken = default);
}
