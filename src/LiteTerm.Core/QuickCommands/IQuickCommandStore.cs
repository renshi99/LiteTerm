namespace LiteTerm.Core.QuickCommands;

public interface IQuickCommandStore
{
    Task<IReadOnlyList<QuickCommandDefinition>> GetQuickCommandsAsync(
        CancellationToken cancellationToken = default);

    Task SaveQuickCommandsAsync(
        IReadOnlyList<QuickCommandDefinition> definitions,
        CancellationToken cancellationToken = default);
}
