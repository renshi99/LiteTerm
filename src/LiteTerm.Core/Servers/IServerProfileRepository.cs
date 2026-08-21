namespace LiteTerm.Core.Servers;

public interface IServerProfileRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServerProfile>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ServerProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveAsync(ServerProfile profile, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ServerCredential?> GetCredentialAsync(Guid serverId, CancellationToken cancellationToken = default);

    Task SaveCredentialAsync(ServerCredential credential, CancellationToken cancellationToken = default);

    Task DeleteCredentialAsync(Guid serverId, CancellationToken cancellationToken = default);
}
