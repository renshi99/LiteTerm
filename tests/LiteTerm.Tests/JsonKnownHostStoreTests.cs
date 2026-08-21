using LiteTerm.Core.Connections;
using LiteTerm.Infrastructure.Ssh;

namespace LiteTerm.Tests;

public sealed class JsonKnownHostStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "LiteTerm.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Verify_WhenHostWasTrusted_ReturnsTrustedAcrossStoreInstances()
    {
        var path = Path.Combine(_directory, "known_hosts.json");
        var hostKey = new HostKeyInfo("ssh-ed25519", "SHA256:trusted-fingerprint");

        var firstStore = new JsonKnownHostStore(path);
        Assert.Equal(KnownHostVerificationStatus.Unknown, firstStore.Verify("Example.COM", 22, hostKey).Status);
        firstStore.Trust("Example.COM", 22, hostKey);

        var secondStore = new JsonKnownHostStore(path);
        var result = secondStore.Verify("example.com.", 22, hostKey);

        Assert.Equal(KnownHostVerificationStatus.Trusted, result.Status);
        var expectedHost = Assert.IsType<KnownHostEntry>(result.ExpectedHost);
        Assert.Equal("example.com", expectedHost.Host);
    }

    [Fact]
    public void Verify_WhenFingerprintOrAlgorithmChanges_ReturnsMismatch()
    {
        var store = new JsonKnownHostStore(Path.Combine(_directory, "known_hosts.json"));
        store.Trust("server.example.com", 2222, new HostKeyInfo("ssh-ed25519", "SHA256:original"));

        var result = store.Verify("server.example.com", 2222, new HostKeyInfo("rsa-sha2-512", "SHA256:changed"));

        Assert.Equal(KnownHostVerificationStatus.Mismatch, result.Status);
        Assert.Equal("ssh-ed25519", result.ExpectedHost?.Algorithm);
        Assert.Equal("SHA256:original", result.ExpectedHost?.Sha256Fingerprint);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
