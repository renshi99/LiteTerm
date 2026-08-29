using System.Windows;
using System.Windows.Media;
using LiteTerm.Core.Settings;

namespace LiteTerm.App;

internal static class ApplicationThemeManager
{
    public static void Apply(ApplicationTheme theme)
    {
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(nameof(theme), "应用主题无效。");
        }

        var colors = theme switch
        {
            ApplicationTheme.Dark => new ThemeColors(
                "#111827", "#1F2937", "#0F172A", "#172033", "#374151", "#273449",
                "#F3F4F6", "#D1D5DB", "#9CA3AF", "#64748B", "#93C5FD"),
            ApplicationTheme.Light => new ThemeColors(
                "#F8FAFC", "#E2E8F0", "#FFFFFF", "#F1F5F9", "#CBD5E1", "#E2E8F0",
                "#111827", "#374151", "#6B7280", "#94A3B8", "#1D4ED8"),
            _ => throw new ArgumentOutOfRangeException(nameof(theme), "应用主题无效。")
        };

        SetBrush("WindowBackgroundBrush", colors.WindowBackground);
        SetBrush("PanelBackgroundBrush", colors.PanelBackground);
        SetBrush("ListBackgroundBrush", colors.ListBackground);
        SetBrush("AlternateBackgroundBrush", colors.AlternateBackground);
        SetBrush("BorderBrush", colors.Border);
        SetBrush("SubtleBorderBrush", colors.SubtleBorder);
        SetBrush("PrimaryTextBrush", colors.PrimaryText);
        SetBrush("SecondaryTextBrush", colors.SecondaryText);
        SetBrush("MutedTextBrush", colors.MutedText);
        SetBrush("DimTextBrush", colors.DimText);
        SetBrush("AccentTextBrush", colors.AccentText);
    }

    private static void SetBrush(string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private sealed record ThemeColors(
        string WindowBackground,
        string PanelBackground,
        string ListBackground,
        string AlternateBackground,
        string Border,
        string SubtleBorder,
        string PrimaryText,
        string SecondaryText,
        string MutedText,
        string DimText,
        string AccentText);
}
