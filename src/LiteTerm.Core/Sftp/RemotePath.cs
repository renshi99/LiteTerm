namespace LiteTerm.Core.Sftp;

/// <summary>
/// 提供不依赖本机 Windows 路径规则的远程 POSIX 路径运算。
/// </summary>
public static class RemotePath
{
    public static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".." || name.Contains('/') || name.Contains('\0'))
        {
            throw new ArgumentException("远程名称不能是 . 或 ..，且不能包含 / 或空字符。", nameof(name));
        }
    }

    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var absolute = path.StartsWith("/", StringComparison.Ordinal);
        var segments = new List<string>();

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0 && segments[^1] != "..")
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                else if (!absolute)
                {
                    segments.Add(segment);
                }

                continue;
            }

            segments.Add(segment);
        }

        if (absolute)
        {
            return segments.Count == 0 ? "/" : $"/{string.Join('/', segments)}";
        }

        return segments.Count == 0 ? "." : string.Join('/', segments);
    }

    public static string Combine(string directory, string child)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(child);

        return child.StartsWith("/", StringComparison.Ordinal)
            ? Normalize(child)
            : Normalize($"{directory.TrimEnd('/')}/{child}");
    }

    public static string GetParent(string path)
    {
        var normalized = Normalize(path);
        if (normalized is "/" or ".")
        {
            return normalized;
        }

        var separatorIndex = normalized.LastIndexOf('/');
        if (separatorIndex < 0)
        {
            return ".";
        }

        return separatorIndex == 0 ? "/" : normalized[..separatorIndex];
    }

    public static string GetName(string path)
    {
        var normalized = Normalize(path);
        if (normalized is "/" or ".")
        {
            return string.Empty;
        }

        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex < 0 ? normalized : normalized[(separatorIndex + 1)..];
    }
}
