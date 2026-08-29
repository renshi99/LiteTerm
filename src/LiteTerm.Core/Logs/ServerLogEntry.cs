using LiteTerm.Core.QuickCommands;

namespace LiteTerm.Core.Logs;

/// <summary>
/// 关联到一条已保存服务器资料的常用远程日志入口。
/// </summary>
public sealed record ServerLogEntry(Guid Id, Guid ServerId, string Name, string RemotePath)
{
    public const int MaximumCountPerServer = 100;
    public const int MaximumNameLength = 80;

    public ServerLogEntry Normalize()
    {
        if (Id == Guid.Empty)
        {
            throw new ArgumentException("日志入口标识不能为空。", nameof(Id));
        }

        if (ServerId == Guid.Empty)
        {
            throw new ArgumentException("日志入口必须属于已保存的服务器。", nameof(ServerId));
        }

        var normalizedName = Name?.Trim() ?? string.Empty;
        if (normalizedName.Length is 0 or > MaximumNameLength)
        {
            throw new ArgumentException($"日志名称长度必须为 1～{MaximumNameLength} 个字符。", nameof(Name));
        }

        var normalizedPath = LiteTerm.Core.Sftp.RemotePath.Normalize(RemotePath);
        if (!normalizedPath.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("日志路径必须是以 / 开头的绝对 POSIX 路径。", nameof(RemotePath));
        }

        _ = QuickCommandTemplate.EscapePosixArgument(normalizedPath);
        return this with { Name = normalizedName, RemotePath = normalizedPath };
    }

    public static IReadOnlyList<ServerLogEntry> NormalizeAll(
        Guid serverId,
        IEnumerable<ServerLogEntry> entries)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("服务器标识不能为空。", nameof(serverId));
        }

        ArgumentNullException.ThrowIfNull(entries);
        var normalized = entries.Select(entry =>
        {
            ArgumentNullException.ThrowIfNull(entry);
            var value = entry.Normalize();
            if (value.ServerId != serverId)
            {
                throw new ArgumentException("日志入口必须属于同一服务器。", nameof(entries));
            }

            return value;
        }).ToArray();

        if (normalized.Length > MaximumCountPerServer)
        {
            throw new ArgumentException(
                $"每台服务器最多保存 {MaximumCountPerServer} 条日志入口。",
                nameof(entries));
        }

        if (normalized.Select(entry => entry.Id).Distinct().Count() != normalized.Length)
        {
            throw new ArgumentException("日志入口标识不能重复。", nameof(entries));
        }

        if (normalized.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != normalized.Length)
        {
            throw new ArgumentException("同一服务器的日志名称不能重复。", nameof(entries));
        }

        return normalized;
    }
}
