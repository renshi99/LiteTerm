using LiteTerm.Core.Connections;
using Renci.SshNet;

namespace LiteTerm.Infrastructure.Ssh;

internal static class SshConnectionInfoFactory
{
    public static ConnectionInfo Create(SshConnectionOptions options)
    {
        AuthenticationMethod authentication = options.AuthenticationType switch
        {
            SshAuthenticationType.Password => new PasswordAuthenticationMethod(
                options.Username,
                options.Password ?? string.Empty),
            SshAuthenticationType.PrivateKey => new PrivateKeyAuthenticationMethod(
                options.Username,
                CreatePrivateKeyFile(options)),
            _ => throw new ArgumentOutOfRangeException(nameof(options.AuthenticationType))
        };

        return new ConnectionInfo(options.Host, options.Port, options.Username, authentication)
        {
            Timeout = options.ConnectTimeout
        };
    }

    private static PrivateKeyFile CreatePrivateKeyFile(SshConnectionOptions options)
    {
        return string.IsNullOrEmpty(options.PrivateKeyPassphrase)
            ? new PrivateKeyFile(options.PrivateKeyPath!)
            : new PrivateKeyFile(options.PrivateKeyPath!, options.PrivateKeyPassphrase);
    }
}
