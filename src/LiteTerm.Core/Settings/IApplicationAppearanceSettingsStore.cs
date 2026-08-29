namespace LiteTerm.Core.Settings;

public interface IApplicationAppearanceSettingsStore : ITerminalAppearanceSettingsStore
{
    Task<ApplicationTheme> GetApplicationThemeAsync(CancellationToken cancellationToken = default);

    Task SaveApplicationAppearanceAsync(
        ApplicationTheme applicationTheme,
        TerminalAppearanceSettings terminalAppearance,
        CancellationToken cancellationToken = default);
}
