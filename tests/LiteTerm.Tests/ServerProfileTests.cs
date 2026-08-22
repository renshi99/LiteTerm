using LiteTerm.Core.Servers;
using LiteTerm.Core.Connections;

namespace LiteTerm.Tests;

public sealed class ServerProfileTests
{
    private static readonly ServerProfile SearchProfile = new(
        Guid.NewGuid(),
        "生产 Web",
        "华东",
        "10.20.30.40",
        22,
        "deploy",
        SshAuthenticationType.Password,
        null,
        null,
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        null,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        null);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("生产")]
    [InlineData("华东")]
    [InlineData("10.20")]
    [InlineData("DEPLOY")]
    [InlineData("生产 deploy")]
    public void MatchesSearch_ReturnsTrueForMatchingFields(string? searchText)
    {
        Assert.True(SearchProfile.MatchesSearch(searchText));
    }

    [Theory]
    [InlineData("测试")]
    [InlineData("生产 root")]
    [InlineData("10.30")]
    public void MatchesSearch_ReturnsFalseWhenAnyTermDoesNotMatch(string searchText)
    {
        Assert.False(SearchProfile.MatchesSearch(searchText));
    }

    [Theory]
    [InlineData(null, "deploy", "ssh.example.com", "deploy@ssh.example.com")]
    [InlineData("   ", " admin ", " 10.0.0.8 ", "admin@10.0.0.8")]
    [InlineData(" 生产服务器 ", "deploy", "ssh.example.com", "生产服务器")]
    public void ResolveName_UsesRequestedNameOrUsernameAndHost(
        string? name,
        string username,
        string host,
        string expected)
    {
        Assert.Equal(expected, ServerProfile.ResolveName(name, username, host));
    }

    [Theory]
    [InlineData(false, new string[0], "deploy@host")]
    [InlineData(false, new[] { "deploy@host" }, "deploy@host (2)")]
    [InlineData(true, new[] { "生产服务器" }, "deploy@host (2)")]
    [InlineData(true, new[] { "deploy@host", "deploy@host (2)", "deploy@host (4)" }, "deploy@host (3)")]
    public void ResolveAvailableName_AppendsFirstAvailableSuffix(
        bool appendSuffix,
        string[] existingNames,
        string expected)
    {
        Assert.Equal(
            expected,
            ServerProfile.ResolveAvailableName("deploy@host", existingNames, appendSuffix));
    }
}
