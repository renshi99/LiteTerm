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

    [Fact]
    public void CreateCopy_CopiesConnectionFieldsButResetsIdentityAndConnectionHistory()
    {
        var newId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddMinutes(1);
        var source = SearchProfile with { LastConnectedAt = DateTimeOffset.UtcNow };

        var copy = source.CreateCopy(newId, [source.Name], createdAt);

        Assert.Equal(newId, copy.Id);
        Assert.Equal("生产 Web - 副本", copy.Name);
        Assert.Equal(source.GroupName, copy.GroupName);
        Assert.Equal(source.Host, copy.Host);
        Assert.Equal(source.Port, copy.Port);
        Assert.Equal(source.Username, copy.Username);
        Assert.Equal(source.AuthenticationType, copy.AuthenticationType);
        Assert.Equal(createdAt, copy.CreatedAt);
        Assert.Equal(createdAt, copy.UpdatedAt);
        Assert.Null(copy.LastConnectedAt);
    }

    [Fact]
    public void CreateCopy_AppendsFirstAvailableSuffixForRepeatedCopies()
    {
        var copy = SearchProfile.CreateCopy(
            Guid.NewGuid(),
            ["生产 Web", "生产 Web - 副本", "生产 Web - 副本 (2)", "生产 Web - 副本 (4)"],
            DateTimeOffset.UtcNow);

        Assert.Equal("生产 Web - 副本 (3)", copy.Name);
    }

    [Fact]
    public void CreateCopy_RejectsEmptyIdentity()
    {
        Assert.Throws<ArgumentException>(() => SearchProfile.CreateCopy(
            Guid.Empty,
            [],
            DateTimeOffset.UtcNow));
    }
}
