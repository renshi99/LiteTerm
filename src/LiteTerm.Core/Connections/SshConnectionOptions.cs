namespace LiteTerm.Core.Connections;

public sealed record SshConnectionOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string Username { get; init; }
    public SshAuthenticationType AuthenticationType { get; init; } = SshAuthenticationType.Password;
    public string? Password { get; init; }
    public string? PrivateKeyPath { get; init; }
    public string? PrivateKeyPassphrase { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("主机不能为空。", nameof(Host));
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "端口必须介于 1 和 65535 之间。");
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            throw new ArgumentException("用户名不能为空。", nameof(Username));
        }

        if (AuthenticationType == SshAuthenticationType.Password && Password is null)
        {
            throw new ArgumentException("密码认证需要提供密码。", nameof(Password));
        }

        if (AuthenticationType == SshAuthenticationType.PrivateKey && string.IsNullOrWhiteSpace(PrivateKeyPath))
        {
            throw new ArgumentException("私钥认证需要提供私钥路径。", nameof(PrivateKeyPath));
        }

        if (ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout), "连接超时必须大于零。");
        }
    }
}
