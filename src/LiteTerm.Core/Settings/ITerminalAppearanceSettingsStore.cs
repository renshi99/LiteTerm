namespace LiteTerm.Core.Settings;

public interface ITerminalAppearanceSettingsStore
{
    Task<TerminalAppearanceSettings> GetTerminalAppearanceAsync(CancellationToken cancellationToken = default);

    Task SaveTerminalAppearanceAsync(
        TerminalAppearanceSettings settings,
        CancellationToken cancellationToken = default);
}
