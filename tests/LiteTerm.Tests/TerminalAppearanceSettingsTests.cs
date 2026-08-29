using LiteTerm.Core.Settings;

namespace LiteTerm.Tests;

public sealed class TerminalAppearanceSettingsTests
{
    [Theory]
    [InlineData("red", "#000000")]
    [InlineData("#FFFFFF", "#FFFFFF")]
    [InlineData("#12345", "#000000")]
    public void Normalize_WhenColorsAreInvalid_ThrowsArgumentException(string foreground, string background)
    {
        var settings = new TerminalAppearanceSettings(foreground, background);

        Assert.Throws<ArgumentException>(() => settings.Normalize());
    }

    [Fact]
    public void Normalize_UppercasesValidColors()
    {
        var settings = new TerminalAppearanceSettings(
            "#abcdef",
            "#01020a",
            "  Cascadia Mono, Consolas, monospace  ",
            16,
            12000);

        Assert.Equal(
            new TerminalAppearanceSettings("#ABCDEF", "#01020A", "Cascadia Mono, Consolas, monospace", 16, 12000),
            settings.Normalize());
    }

    [Theory]
    [InlineData("", 14, 5000)]
    [InlineData("Consolas", 7, 5000)]
    [InlineData("Consolas", 33, 5000)]
    [InlineData("Consolas", 14, 99)]
    [InlineData("Consolas", 14, 100001)]
    public void Normalize_WhenDisplaySettingIsInvalid_ThrowsArgumentException(
        string fontFamily,
        int fontSize,
        int scrollback)
    {
        var settings = new TerminalAppearanceSettings("#FFFFFF", "#000000", fontFamily, fontSize, scrollback);

        Assert.ThrowsAny<ArgumentException>(() => settings.Normalize());
    }
}
