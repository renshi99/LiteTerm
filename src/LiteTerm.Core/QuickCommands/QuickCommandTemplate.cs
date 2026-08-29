using System.Text.RegularExpressions;

namespace LiteTerm.Core.QuickCommands;

public static partial class QuickCommandTemplate
{
    private const string PathVariable = "path";
    private const int MaximumPathLength = 4096;

    public static bool RequiresPath(string commandTemplate)
    {
        Validate(commandTemplate);
        return TemplateVariableRegex().Matches(commandTemplate).Count > 0;
    }

    public static string Render(string commandTemplate, string? remotePath)
    {
        Validate(commandTemplate);
        var requiresPath = RequiresPath(commandTemplate);
        if (requiresPath && string.IsNullOrEmpty(remotePath))
        {
            throw new ArgumentException("该命令需要填写远程路径。", nameof(remotePath));
        }

        var escapedPath = requiresPath ? EscapePosixArgument(remotePath!) : string.Empty;
        return TemplateVariableRegex().Replace(commandTemplate.Trim(), match =>
            string.Equals(match.Groups[1].Value, PathVariable, StringComparison.Ordinal)
                ? escapedPath
                : throw new FormatException($"不支持模板变量 {{{match.Groups[1].Value}}}。"));
    }

    public static string EscapePosixArgument(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > MaximumPathLength)
        {
            throw new ArgumentException($"远程路径不能超过 {MaximumPathLength} 个字符。", nameof(value));
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("远程路径不能包含控制字符。", nameof(value));
        }

        return $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
    }

    public static void Validate(string commandTemplate)
    {
        if (string.IsNullOrWhiteSpace(commandTemplate))
        {
            throw new ArgumentException("命令模板不能为空。", nameof(commandTemplate));
        }

        if (commandTemplate.Length > QuickCommandDefinition.MaximumTemplateLength)
        {
            throw new ArgumentException(
                $"命令模板不能超过 {QuickCommandDefinition.MaximumTemplateLength} 个字符。",
                nameof(commandTemplate));
        }

        if (commandTemplate.Any(char.IsControl))
        {
            throw new ArgumentException("命令模板必须是单行，且不能包含控制字符。", nameof(commandTemplate));
        }

        foreach (Match match in TemplateVariableRegex().Matches(commandTemplate))
        {
            if (!string.Equals(match.Groups[1].Value, PathVariable, StringComparison.Ordinal))
            {
                throw new FormatException($"不支持模板变量 {{{match.Groups[1].Value}}}；当前仅支持 {{path}}。");
            }
        }
    }

    [GeneratedRegex(@"(?<!\$)\{([A-Za-z][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateVariableRegex();
}
