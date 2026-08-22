using LiteTerm.Core.Connections;

namespace LiteTerm.Core.Servers;

/// <summary>
/// 可公开保存的服务器连接资料，不包含密码或私钥口令。
/// </summary>
public sealed record ServerProfile(
    Guid Id,
    string Name,
    string? GroupName,
    string Host,
    int Port,
    string Username,
    SshAuthenticationType AuthenticationType,
    string? PrivateKeyPath,
    string? DefaultRemotePath,
    TimeSpan ConnectTimeout,
    TimeSpan KeepAliveInterval,
    string? Remark,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastConnectedAt)
{
    /// <summary>
    /// 判断服务器资料是否匹配名称、分组、地址或用户名中的全部搜索词。
    /// </summary>
    public bool MatchesSearch(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        var searchableFields = new[] { Name, GroupName, Host, Username };
        return searchText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => searchableFields.Any(field =>
                field?.Contains(term, StringComparison.OrdinalIgnoreCase) == true));
    }

    public static string ResolveName(string? name, string username, string host)
    {
        return string.IsNullOrWhiteSpace(name)
            ? $"{username.Trim()}@{host.Trim()}"
            : name.Trim();
    }

    public static string ResolveAvailableName(
        string baseName,
        IEnumerable<string> existingNames,
        bool appendSuffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentNullException.ThrowIfNull(existingNames);

        var normalizedBaseName = baseName.Trim();
        var names = existingNames
            .Select(existingName => existingName.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!appendSuffix && !names.Contains(normalizedBaseName))
        {
            return normalizedBaseName;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{normalizedBaseName} ({suffix})";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    public void Validate()
    {
        if (Id == Guid.Empty)
        {
            throw new ArgumentException("服务器标识不能为空。", nameof(Id));
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new ArgumentException("服务器名称不能为空。", nameof(Name));
        }

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

        if (AuthenticationType == SshAuthenticationType.PrivateKey && string.IsNullOrWhiteSpace(PrivateKeyPath))
        {
            throw new ArgumentException("私钥认证需要提供私钥路径。", nameof(PrivateKeyPath));
        }

        if (ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout), "连接超时必须大于零。");
        }

        if (KeepAliveInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(KeepAliveInterval), "KeepAlive 间隔不能小于零。");
        }

        if (UpdatedAt < CreatedAt)
        {
            throw new ArgumentException("更新时间不能早于创建时间。", nameof(UpdatedAt));
        }
    }
}
