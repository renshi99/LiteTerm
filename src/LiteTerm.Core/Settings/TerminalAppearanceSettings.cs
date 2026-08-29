using System.Text.RegularExpressions;

namespace LiteTerm.Core.Settings;

/// <summary>
/// 终端配色和文字显示参数；颜色使用不带透明度的十六进制格式。
/// </summary>
public sealed partial record TerminalAppearanceSettings(
    string ForegroundColor,
    string BackgroundColor,
    string FontFamily = "Cascadia Mono, Consolas, monospace",
    int FontSize = 14,
    int Scrollback = 5000)
{
    public const int MinimumFontSize = 8;
    public const int MaximumFontSize = 32;
    public const int MinimumScrollback = 100;
    public const int MaximumScrollback = 100000;

    public static TerminalAppearanceSettings Default { get; } = new(
        "#F1F5F9",
        "#050816",
        "Cascadia Mono, Consolas, monospace",
        14,
        5000);

    public void Validate()
    {
        ValidateColor(ForegroundColor, nameof(ForegroundColor));
        ValidateColor(BackgroundColor, nameof(BackgroundColor));

        if (string.Equals(ForegroundColor, BackgroundColor, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("终端文字颜色和背景颜色不能相同。");
        }

        if (string.IsNullOrWhiteSpace(FontFamily))
        {
            throw new ArgumentException("终端字体不能为空。", nameof(FontFamily));
        }

        if (FontFamily.Length > 128 || FontFamily.Any(char.IsControl))
        {
            throw new ArgumentException("终端字体名称无效或过长。", nameof(FontFamily));
        }

        if (FontSize is < MinimumFontSize or > MaximumFontSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(FontSize),
                $"终端字号必须介于 {MinimumFontSize} 和 {MaximumFontSize} 之间。");
        }

        if (Scrollback is < MinimumScrollback or > MaximumScrollback)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Scrollback),
                $"终端滚动行数必须介于 {MinimumScrollback} 和 {MaximumScrollback} 之间。");
        }
    }

    public TerminalAppearanceSettings Normalize()
    {
        Validate();
        return new TerminalAppearanceSettings(
            ForegroundColor.ToUpperInvariant(),
            BackgroundColor.ToUpperInvariant(),
            FontFamily.Trim(),
            FontSize,
            Scrollback);
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
