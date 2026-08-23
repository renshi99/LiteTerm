using LiteTerm.Core.Sftp;

namespace LiteTerm.Tests;

public sealed class RemotePathTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/var//log/./app", "/var/log/app")]
    [InlineData("/var/log/../tmp", "/var/tmp")]
    [InlineData("../../var/../tmp", "../../tmp")]
    [InlineData("app/..", ".")]
    public void Normalize_UsesPosixRules(string path, string expected)
    {
        Assert.Equal(expected, RemotePath.Normalize(path));
    }

    [Theory]
    [InlineData("/var/log", "app", "/var/log/app")]
    [InlineData("/var/log/", "../tmp", "/var/tmp")]
    [InlineData("/var/log", "/etc", "/etc")]
    public void Combine_ResolvesChildAgainstRemoteDirectory(
        string directory,
        string child,
        string expected)
    {
        Assert.Equal(expected, RemotePath.Combine(directory, child));
    }

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/var/log", "/var")]
    [InlineData("/var", "/")]
    [InlineData("logs", ".")]
    public void GetParent_DoesNotNavigateAboveRoot(string path, string expected)
    {
        Assert.Equal(expected, RemotePath.GetParent(path));
    }
}
