using System.Text;
using System.Text.Json;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Security;
using LiteTerm.Core.Servers;
using LiteTerm.Core.Settings;
using LiteTerm.Infrastructure.Data;
using Microsoft.Data.Sqlite;

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

    [Fact]
    public async Task SaveWithCredentialAsync_CreatesAndUpdatesProfileAndCredentialTogether()
    {
        var databasePath = Path.Combine(_directory, "liteterm.db");
        var repository = new SqliteServerProfileRepository(databasePath, new TestSecretProtector());
        var profile = CreateProfile();

        await repository.SaveWithCredentialAsync(profile, new ServerCredential(profile.Id, "initial-password", null));

        var updatedProfile = profile with
        {
            Name = "更新后的服务器",
            AuthenticationType = SshAuthenticationType.PrivateKey,
            PrivateKeyPath = "C:\\Keys\\replacement_ed25519",
            UpdatedAt = profile.UpdatedAt.AddMinutes(1)
        };
        var updatedCredential = new ServerCredential(profile.Id, null, "replacement-passphrase");
        await repository.SaveWithCredentialAsync(updatedProfile, updatedCredential);

        Assert.Equal(updatedProfile, await repository.GetByIdAsync(profile.Id));
        Assert.Equal(updatedCredential, await repository.GetCredentialAsync(profile.Id));
    }

    [Fact]
    public async Task SaveWithCredentialAsync_PersistsCopiedProfileWithIndependentCredentialReference()
    {
        var databasePath = Path.Combine(_directory, "liteterm.db");
        var repository = new SqliteServerProfileRepository(databasePath, new TestSecretProtector());
        var source = CreateProfile() with { LastConnectedAt = DateTimeOffset.UtcNow };
        var sourceCredential = new ServerCredential(source.Id, null, "copied-passphrase");
        await repository.SaveWithCredentialAsync(source, sourceCredential);

        var copy = source.CreateCopy(Guid.NewGuid(), [source.Name], DateTimeOffset.UtcNow.AddMinutes(1));
        var copiedCredential = sourceCredential with { ServerId = copy.Id };
        await repository.SaveWithCredentialAsync(copy, copiedCredential);

        Assert.Equal(source, await repository.GetByIdAsync(source.Id));
        Assert.Equal(sourceCredential, await repository.GetCredentialAsync(source.Id));
        Assert.Equal(copy, await repository.GetByIdAsync(copy.Id));
        Assert.Equal(copiedCredential, await repository.GetCredentialAsync(copy.Id));
    }

    [Fact]
    public async Task SaveWithCredentialAsync_RejectsCredentialForAnotherServerBeforeWriting()
    {
        var databasePath = Path.Combine(_directory, "liteterm.db");
        var repository = new SqliteServerProfileRepository(databasePath, new TestSecretProtector());
        var profile = CreateProfile();

        await Assert.ThrowsAsync<ArgumentException>(() => repository.SaveWithCredentialAsync(
            profile,
            new ServerCredential(Guid.NewGuid(), "password", null)));

        Assert.Null(await repository.GetByIdAsync(profile.Id));
    }

    [Fact]
    public async Task KnownHosts_ArePersistedAndMismatchesAreRejectedAcrossRepositoryInstances()
    {
        var databasePath = Path.Combine(_directory, "liteterm.db");
        var hostKey = new HostKeyInfo("ssh-ed25519", "SHA256:trusted");
        var firstRepository = new SqliteServerProfileRepository(databasePath, new TestSecretProtector());
        await firstRepository.InitializeAsync();

        Assert.Equal(KnownHostVerificationStatus.Unknown, firstRepository.Verify("Example.COM", 22, hostKey).Status);
        firstRepository.Trust("Example.COM", 22, hostKey);

        var secondRepository = new SqliteServerProfileRepository(databasePath, new TestSecretProtector());
        await secondRepository.InitializeAsync();
        Assert.Equal(KnownHostVerificationStatus.Trusted, secondRepository.Verify("example.com.", 22, hostKey).Status);

        var mismatch = secondRepository.Verify(
            "example.com",
            22,
            new HostKeyInfo("rsa-sha2-512", "SHA256:changed"));
        Assert.Equal(KnownHostVerificationStatus.Mismatch, mismatch.Status);
        Assert.Equal(hostKey.Sha256Fingerprint, mismatch.ExpectedHost?.Sha256Fingerprint);
    }

    [Fact]
    public async Task InitializeAsync_ImportsLegacyJsonKnownHostsWithoutOverwritingDatabaseTrust()
    {
        var databasePath = Path.Combine(_directory, "liteterm.db");
        var legacyPath = Path.Combine(_directory, "known_hosts.json");
        var legacyKey = new HostKeyInfo("ssh-ed25519", "SHA256:legacy");
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            legacyPath,
            JsonSerializer.Serialize(new[] { new KnownHostEntry("Legacy.Example.COM", 2222, legacyKey.Algorithm, legacyKey.Sha256Fingerprint) }));

        var repository = new SqliteServerProfileRepository(databasePath, new TestSecretProtector(), legacyPath);
        await repository.InitializeAsync();
        Assert.Equal(KnownHostVerificationStatus.Trusted, repository.Verify("legacy.example.com", 2222, legacyKey).Status);

        var replacementKey = new HostKeyInfo("ssh-ed25519", "SHA256:replacement");
        repository.Trust("legacy.example.com", 2222, replacementKey);
        var restartedRepository = new SqliteServerProfileRepository(databasePath, new TestSecretProtector(), legacyPath);
        await restartedRepository.InitializeAsync();
        Assert.Equal(KnownHostVerificationStatus.Trusted, restartedRepository.Verify("legacy.example.com", 2222, replacementKey).Status);
    }

    [Fact]
    public async Task InitializeAsync_UpgradesVersionOneDatabaseWithoutLosingProfiles()
    {
        var databasePath = Path.Combine(_directory, "liteterm.db");
        var profile = CreateProfile();
        var initialRepository = new SqliteServerProfileRepository(databasePath, new TestSecretProtector());
        await initialRepository.SaveAsync(profile);

        var connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE known_host;
                DROP TABLE app_setting;
                DELETE FROM schema_migration WHERE version >= 2;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var upgradedRepository = new SqliteServerProfileRepository(databasePath, new TestSecretProtector());
        await upgradedRepository.InitializeAsync();

        Assert.Equal(profile, await upgradedRepository.GetByIdAsync(profile.Id));
        Assert.Equal(
            KnownHostVerificationStatus.Unknown,
            upgradedRepository.Verify("new.example.com", 22, new HostKeyInfo("ssh-ed25519", "SHA256:new")).Status);
    }

    [Fact]
    public async Task TerminalAppearance_UsesDefaultsAndPersistsNormalizedCustomColors()
    {
        var databasePath = Path.Combine(_directory, "liteterm.db");
        var firstRepository = new SqliteServerProfileRepository(databasePath, new TestSecretProtector());

        Assert.Equal(TerminalAppearanceSettings.Default, await firstRepository.GetTerminalAppearanceAsync());

        await firstRepository.SaveTerminalAppearanceAsync(new TerminalAppearanceSettings("#abcdef", "#102030"));
        var secondRepository = new SqliteServerProfileRepository(databasePath, new TestSecretProtector());

        Assert.Equal(
            new TerminalAppearanceSettings("#ABCDEF", "#102030"),
            await secondRepository.GetTerminalAppearanceAsync());
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
