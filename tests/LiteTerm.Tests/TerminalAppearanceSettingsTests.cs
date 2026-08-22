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
        var settings = new TerminalAppearanceSettings("#abcdef", "#01020a");

        Assert.Equal(new TerminalAppearanceSettings("#ABCDEF", "#01020A"), settings.Normalize());
    }
}
