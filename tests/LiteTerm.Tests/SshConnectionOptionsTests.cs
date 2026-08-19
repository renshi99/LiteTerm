using LiteTerm.Core.Connections;

namespace LiteTerm.Tests;

public sealed class SshConnectionOptionsTests
{
    [Fact]
    public void Validate_AcceptsPasswordConnection()
    {
        var options = new SshConnectionOptions
        {
            Host = "server.example.com",
            Username = "tester",
            Password = "secret"
        };

        options.Validate();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Validate_RejectsInvalidPort(int port)
    {
        var options = new SshConnectionOptions
        {
            Host = "server.example.com",
            Port = port,
            Username = "tester",
            Password = "secret"
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Validate_RequiresPrivateKeyPath()
    {
        var options = new SshConnectionOptions
        {
            Host = "server.example.com",
            Username = "tester",
            AuthenticationType = SshAuthenticationType.PrivateKey
        };

        Assert.Throws<ArgumentException>(options.Validate);
    }
}
