using System.Text;
using System.Text.Json;
using LiteTerm.Core.Connections;

namespace LiteTerm.Infrastructure.Diagnostics;

/// <summary>
/// 将脱敏连接事件写入有界 JSON Lines 文件；日志故障由调用方按最佳努力处理。
/// </summary>
public sealed class FileConnectionDiagnosticLogger : IConnectionDiagnosticLogger
{
    public const long DefaultMaximumFileBytes = 1024 * 1024;

    private readonly string _path;
    private readonly string _archivePath;
    private readonly long _maximumFileBytes;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public FileConnectionDiagnosticLogger(
        string path,
        long maximumFileBytes = DefaultMaximumFileBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }

        _path = Path.GetFullPath(path);
        _archivePath = $"{_path}.previous";
        _maximumFileBytes = maximumFileBytes;
    }

    public async ValueTask WriteAsync(
        ConnectionDiagnosticEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("诊断日志路径必须包含有效目录。");
            Directory.CreateDirectory(directory);
            RotateIfNeeded();

            var json = JsonSerializer.Serialize(entry);
            await using var stream = new FileStream(
                _path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < _maximumFileBytes)
        {
            return;
        }

        File.Move(_path, _archivePath, overwrite: true);
    }
}
