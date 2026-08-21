using System.Text;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Security;
using LiteTerm.Core.Servers;
using LiteTerm.Infrastructure.Data;

namespace LiteTerm.Tests;

public sealed class SqliteServerProfileRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LiteTerm.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAsync_PersistsPublicProfileAndProtectedCredentialsAcrossRepositoryInstances()
    {
        const string password = "correct horse battery staple";
        const string privateKeyPassphrase = "private-key-passphrase";
        var databasePath = Path.Combine(_directory, "data", "liteterm.db");
        var profile = CreateProfile();
        var credential = new ServerCredential(profile.Id, password, privateKeyPassphrase);

        var firstRepository = new SqliteServerProfileRepository(databasePath, new TestSecretProtector());
        await firstRepository.SaveAsync(profile);
        await firstRepository.SaveCredentialAsync(credential);

        var secondRepository = new SqliteServerProfileRepository(databasePath, new TestSecretProtector());
        var savedProfile = await secondRepository.GetByIdAsync(profile.Id);
        var savedCredential = await secondRepository.GetCredentialAsync(profile.Id);

        Assert.Equal(profile, savedProfile);
        Assert.Equal(credential, savedCredential);
        var databaseContents = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(databasePath));
        Assert.DoesNotContain(password, databaseContents);
        Assert.DoesNotContain(privateKeyPassphrase, databaseContents);
    }

    [Fact]
    public async Task DeleteAsync_RemovesAssociatedCredential()
    {
        var databasePath = Path.Combine(_directory, "liteterm.db");
        var profile = CreateProfile();
        var repository = new SqliteServerProfileRepository(databasePath, new TestSecretProtector());
        await repository.SaveAsync(profile);
        await repository.SaveCredentialAsync(new ServerCredential(profile.Id, "password", null));

        await repository.DeleteAsync(profile.Id);

        Assert.Null(await repository.GetByIdAsync(profile.Id));
        Assert.Null(await repository.GetCredentialAsync(profile.Id));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ServerProfile CreateProfile()
    {
        var createdAt = DateTimeOffset.UtcNow;
        return new ServerProfile(
            Guid.NewGuid(),
            "测试服务器",
            "开发",
            "ssh.example.com",
            22,
            "deploy",
            SshAuthenticationType.PrivateKey,
            "C:\\Keys\\id_ed25519",
            "/srv/application",
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
            "仅用于测试",
            createdAt,
            createdAt,
            null);
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext);

        public byte[] Unprotect(ReadOnlySpan<byte> protectedData) => Transform(protectedData);

        private static byte[] Transform(ReadOnlySpan<byte> data)
        {
            var result = new byte[data.Length];
            for (var index = 0; index < data.Length; index++)
            {
                result[index] = (byte)(data[index] ^ 0xA5);
            }

            return result;
        }
    }
}
