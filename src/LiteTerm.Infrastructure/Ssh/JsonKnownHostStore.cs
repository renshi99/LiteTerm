using System.Text;
using System.Text.Json;
using LiteTerm.Core.Connections;

namespace LiteTerm.Infrastructure.Ssh;

/// <summary>
/// 将已确认的主机身份保存在应用数据目录中的 JSON 文件。
/// </summary>
public sealed class JsonKnownHostStore : IKnownHostStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _filePath;

    public JsonKnownHostStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("已知主机文件路径不能为空。", nameof(filePath));
        }

        _filePath = filePath;
    }

    public KnownHostVerificationResult Verify(string host, int port, HostKeyInfo hostKey)
    {
        var normalizedHost = NormalizeHost(host);
        ValidatePort(port);
        ValidateHostKey(hostKey);

        lock (_gate)
        {
            var expected = LoadEntries()
                .SingleOrDefault(entry => entry.Host == normalizedHost && entry.Port == port);

            if (expected is null)
            {
                return new KnownHostVerificationResult(KnownHostVerificationStatus.Unknown, null);
            }

            var isMatch = string.Equals(expected.Algorithm, hostKey.Algorithm, StringComparison.Ordinal)
                          && string.Equals(expected.Sha256Fingerprint, hostKey.Sha256Fingerprint, StringComparison.Ordinal);
            return new KnownHostVerificationResult(
                isMatch ? KnownHostVerificationStatus.Trusted : KnownHostVerificationStatus.Mismatch,
                expected);
        }
    }

    public void Trust(string host, int port, HostKeyInfo hostKey)
    {
        var normalizedHost = NormalizeHost(host);
        ValidatePort(port);
        ValidateHostKey(hostKey);

        lock (_gate)
        {
            var entries = LoadEntries()
                .Where(entry => entry.Host != normalizedHost || entry.Port != port)
                .ToList();
            entries.Add(new KnownHostEntry(normalizedHost, port, hostKey.Algorithm, hostKey.Sha256Fingerprint));
            SaveEntries(entries);
        }
    }

    private IReadOnlyList<KnownHostEntry> LoadEntries()
    {
        if (!File.Exists(_filePath))
        {
            return Array.Empty<KnownHostEntry>();
        }

        using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var entries = JsonSerializer.Deserialize<List<KnownHostEntry>>(stream, SerializerOptions)
            ?? throw new InvalidDataException("已知主机文件格式无效。");

        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var normalizedHost = NormalizeHost(entry.Host);
            ValidatePort(entry.Port);
            ValidateHostKey(new HostKeyInfo(entry.Algorithm, entry.Sha256Fingerprint));
            if (normalizedHost != entry.Host || !endpoints.Add($"{normalizedHost}\0{entry.Port}"))
            {
                throw new InvalidDataException("已知主机文件包含无效或重复记录。");
            }
        }

        return entries;
    }

    private void SaveEntries(IReadOnlyList<KnownHostEntry> entries)
    {
        var directory = Path.GetDirectoryName(_filePath)
            ?? throw new InvalidOperationException("已知主机文件路径无效。");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetRandomFileName()}");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(JsonSerializer.Serialize(entries, SerializerOptions));
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string NormalizeHost(string host)
    {
        var normalizedHost = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedHost))
        {
            throw new ArgumentException("主机不能为空。", nameof(host));
        }

        return normalizedHost;
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "端口必须介于 1 和 65535 之间。");
        }
    }

    private static void ValidateHostKey(HostKeyInfo hostKey)
    {
        ArgumentNullException.ThrowIfNull(hostKey);
        if (string.IsNullOrWhiteSpace(hostKey.Algorithm) || string.IsNullOrWhiteSpace(hostKey.Sha256Fingerprint))
        {
            throw new ArgumentException("主机密钥信息无效。", nameof(hostKey));
        }
    }
}
