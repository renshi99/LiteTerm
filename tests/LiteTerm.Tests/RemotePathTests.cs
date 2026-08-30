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

    [Theory]
    [InlineData("/", "")]
    [InlineData(".", "")]
    [InlineData("/var/log/application.log", "application.log")]
    [InlineData("logs/应用 日志.log", "应用 日志.log")]
    public void GetName_ReturnsFinalPosixSegment(string path, string expected)
    {
        Assert.Equal(expected, RemotePath.GetName(path));
    }

    [Theory]
    [InlineData("logs")]
    [InlineData("中文 目录")]
    [InlineData("name\\with-backslash")]
    public void ValidateName_AcceptsSinglePosixSegment(string name)
    {
        RemotePath.ValidateName(name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\0b")]
    public void ValidateName_RejectsInvalidSegment(string name)
    {
        Assert.Throws<ArgumentException>(() => RemotePath.ValidateName(name));
    }
}
