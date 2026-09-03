using LiteTerm.Core.Connections;

namespace LiteTerm.Tests;

public sealed class TerminalInitializationFailureTests
{
    [Theory]
    [InlineData(TerminalInitializationFailureKind.RuntimeMissing, "webview2_runtime_missing")]
    [InlineData(TerminalInitializationFailureKind.Timeout, "webview2_initialization_timeout")]
    [InlineData(TerminalInitializationFailureKind.Unknown, "webview2_initialization_failed")]
    public void Failure_UsesStableSafeMetadata(
        TerminalInitializationFailureKind kind,
        string expectedCode)
    {
        var failure = new TerminalInitializationFailure(kind);

        Assert.Equal(expectedCode, failure.Code);
        Assert.NotEmpty(failure.UserMessage);
        Assert.DoesNotContain("Exception", failure.UserMessage, StringComparison.OrdinalIgnoreCase);
    }
}
