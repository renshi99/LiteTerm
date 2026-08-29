namespace LiteTerm.Core.QuickCommands;

/// <summary>
/// 可保存并在执行前展开、预览的终端命令模板。
/// </summary>
public sealed record QuickCommandDefinition(Guid Id, string Name, string CommandTemplate)
{
    public const int MaximumCount = 50;
    public const int MaximumNameLength = 80;
    public const int MaximumTemplateLength = 4096;
    public const string FollowLogTemplate = "tail -n 500 -F -- {path}";
    public const string EnterLogDirectoryTemplate = "cd -- \"$(dirname -- {path})\"";

    public static IReadOnlyList<QuickCommandDefinition> Defaults { get; } =
    [
        new(
            Guid.Parse("bfa2bfcb-68f1-4afb-87e5-e7de7ad4992c"),
            "持续追踪日志（末尾 500 行）",
            FollowLogTemplate),
        new(
            Guid.Parse("fb2bd4cf-a14a-4130-898c-90f02732c54c"),
            "进入日志目录",
            EnterLogDirectoryTemplate)
    ];

    public QuickCommandDefinition Normalize()
    {
        if (Id == Guid.Empty)
        {
            throw new ArgumentException("常用命令标识不能为空。", nameof(Id));
        }

        var normalizedName = Name?.Trim() ?? string.Empty;
        if (normalizedName.Length is 0 or > MaximumNameLength)
        {
            throw new ArgumentException($"命令名称长度必须为 1～{MaximumNameLength} 个字符。", nameof(Name));
        }

        var normalizedTemplate = CommandTemplate?.Trim() ?? string.Empty;
        if (normalizedTemplate.Length is 0 or > MaximumTemplateLength)
        {
            throw new ArgumentException(
                $"命令模板长度必须为 1～{MaximumTemplateLength} 个字符。",
                nameof(CommandTemplate));
        }

        QuickCommandTemplate.Validate(normalizedTemplate);
        return this with { Name = normalizedName, CommandTemplate = normalizedTemplate };
    }

    public static IReadOnlyList<QuickCommandDefinition> NormalizeAll(
        IEnumerable<QuickCommandDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var normalized = definitions.Select(definition =>
        {
            ArgumentNullException.ThrowIfNull(definition);
            return definition.Normalize();
        }).ToArray();

        if (normalized.Length > MaximumCount)
        {
            throw new ArgumentException($"常用命令最多保存 {MaximumCount} 条。", nameof(definitions));
        }

        if (normalized.Select(definition => definition.Id).Distinct().Count() != normalized.Length)
        {
            throw new ArgumentException("常用命令标识不能重复。", nameof(definitions));
        }

        if (normalized.Select(definition => definition.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != normalized.Length)
        {
            throw new ArgumentException("常用命令名称不能重复。", nameof(definitions));
        }

        return normalized;
    }
}
