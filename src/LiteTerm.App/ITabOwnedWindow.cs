namespace LiteTerm.App;

/// <summary>
/// 表示由终端标签拥有、关闭标签时必须等待释放完成的子窗口。
/// </summary>
internal interface ITabOwnedWindow
{
    event EventHandler Closed;

    Task CloseAndWaitAsync();
}
