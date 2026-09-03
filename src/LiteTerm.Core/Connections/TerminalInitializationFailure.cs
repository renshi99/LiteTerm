namespace LiteTerm.Core.Connections;

/// <summary>
/// 表示不会包含本机路径或底层异常消息的终端初始化失败。
/// </summary>
public sealed record TerminalInitializationFailure(TerminalInitializationFailureKind Kind)
{
    public string Code => Kind switch
    {
        TerminalInitializationFailureKind.RuntimeMissing => "webview2_runtime_missing",
        TerminalInitializationFailureKind.Timeout => "webview2_initialization_timeout",
        _ => "webview2_initialization_failed"
    };

    public string UserMessage => Kind switch
    {
        TerminalInitializationFailureKind.RuntimeMissing =>
            "无法启动终端组件。请安装或修复 Microsoft Edge WebView2 Runtime，然后重试终端。",
        TerminalInitializationFailureKind.Timeout =>
            "终端组件未能及时就绪，请重试终端。",
        _ => "终端组件初始化失败，请重试；如问题持续，请查看诊断日志。"
    };
}

public enum TerminalInitializationFailureKind
{
    Unknown,
    RuntimeMissing,
    Timeout
}
