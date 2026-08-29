using LiteTerm.Core.QuickCommands;

namespace LiteTerm.Tests;

public sealed class QuickCommandTemplateTests
{
    [Fact]
    public void Render_QuotesSpacesApostrophesAndShellMetacharactersAsOneArgument()
    {
        var rendered = QuickCommandTemplate.Render(
            "tail -n 500 -F -- {path}",
            "/var/log/my app's;$(danger).log");

        Assert.Equal("tail -n 500 -F -- '/var/log/my app'\"'\"'s;$(danger).log'", rendered);
    }

    [Fact]
    public void Render_ExpandsEveryPathPlaceholder()
    {
        var rendered = QuickCommandTemplate.Render("printf '%s %s' {path} {path}", "/tmp/a b");

        Assert.Equal("printf '%s %s' '/tmp/a b' '/tmp/a b'", rendered);
    }

    [Fact]
    public void Render_RejectsMissingPathAndUnknownVariable()
    {
        Assert.Throws<ArgumentException>(() => QuickCommandTemplate.Render("cat -- {path}", string.Empty));
        Assert.Throws<FormatException>(() => QuickCommandTemplate.Render("cat -- {file}", "/tmp/test"));
    }

    [Fact]
    public void Render_PreservesLeadingAndTrailingSpacesInAValidPosixPath()
    {
        Assert.Equal("cat -- ' /tmp/log '", QuickCommandTemplate.Render("cat -- {path}", " /tmp/log "));
    }

    [Fact]
    public void Render_AllowsShellEnvironmentExpansionWithoutTreatingItAsTemplateVariable()
    {
        Assert.Equal("printf '%s' ${HOME}", QuickCommandTemplate.Render("printf '%s' ${HOME}", null));
    }

    [Fact]
    public void LogEntryTemplates_RenderSpecialPathThroughTheSharedEscaper()
    {
        const string path = "/var/log/应用 app's;$(danger).log";

        Assert.Equal(
            "tail -n 500 -F -- '/var/log/应用 app'\"'\"'s;$(danger).log'",
            QuickCommandTemplate.Render(QuickCommandDefinition.FollowLogTemplate, path));
        Assert.Equal(
            "cd -- \"$(dirname -- '/var/log/应用 app'\"'\"'s;$(danger).log')\"",
            QuickCommandTemplate.Render(QuickCommandDefinition.EnterLogDirectoryTemplate, path));
    }

    [Fact]
    public void Validate_RejectsMultilineTemplateAndPath()
    {
        Assert.Throws<ArgumentException>(() => QuickCommandTemplate.Validate("pwd\nwhoami"));
        Assert.Throws<ArgumentException>(() => QuickCommandTemplate.EscapePosixArgument("/tmp/a\nb"));
        Assert.Throws<ArgumentException>(() => QuickCommandTemplate.EscapePosixArgument("/tmp/\u001b[31m"));
    }

    [Fact]
    public void NormalizeAll_RejectsDuplicateNamesIgnoringCase()
    {
        var commands = new[]
        {
            new QuickCommandDefinition(Guid.NewGuid(), "Tail Log", "pwd"),
            new QuickCommandDefinition(Guid.NewGuid(), "tail log", "whoami")
        };

        Assert.Throws<ArgumentException>(() => QuickCommandDefinition.NormalizeAll(commands));
    }
}
