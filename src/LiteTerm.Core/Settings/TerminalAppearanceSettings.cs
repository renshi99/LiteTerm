using System.Text.RegularExpressions;

namespace LiteTerm.Core.Settings;

/// <summary>
/// 终端文字与背景配色；颜色使用不带透明度的十六进制格式。
/// </summary>
public sealed partial record TerminalAppearanceSettings(string ForegroundColor, string BackgroundColor)
{
    public static TerminalAppearanceSettings Default { get; } = new("#F1F5F9", "#050816");

    public void Validate()
    {
        ValidateColor(ForegroundColor, nameof(ForegroundColor));
        ValidateColor(BackgroundColor, nameof(BackgroundColor));

        if (string.Equals(ForegroundColor, BackgroundColor, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("终端文字颜色和背景颜色不能相同。");
        }
    }

    public TerminalAppearanceSettings Normalize()
    {
        Validate();
        return new TerminalAppearanceSettings(ForegroundColor.ToUpperInvariant(), BackgroundColor.ToUpperInvariant());
    }

    private static void ValidateColor(string color, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(color) || !HexColorPattern().IsMatch(color))
        {
            throw new ArgumentException("颜色必须使用 #RRGGBB 格式。", parameterName);
        }
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColorPattern();
}
