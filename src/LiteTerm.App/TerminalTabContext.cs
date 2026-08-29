using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Terminal;
using Microsoft.Web.WebView2.Wpf;

namespace LiteTerm.App;

/// <summary>
/// Owns all mutable state and resources for one terminal tab.
/// </summary>
internal sealed class TerminalTabContext : IAsyncDisposable
{
    private const int MaximumOutputBatchBytes = 64 * 1024;
    private readonly DispatcherTimer _outputTimer;
    private readonly List<SftpWindow> _sftpWindows = [];
    private readonly TaskCompletionSource _terminalReadyCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private int _disposed;

    public TerminalTabContext(ISshTerminalSession session, WebView2 webView, Dispatcher dispatcher)
    {
        Session = session;
        WebView = webView;
        LifetimeToken = _lifetimeCancellation.Token;
        OutputBuffer = new BoundedTerminalOutputBuffer(1024 * 1024);
        _outputTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Background,
            FlushOutput,
            dispatcher);
        _outputTimer.Start();
    }

    public ISshTerminalSession Session { get; }
    public WebView2 WebView { get; }
    public CancellationToken LifetimeToken { get; }
    public BoundedTerminalOutputBuffer OutputBuffer { get; }
    public CancellationTokenSource? ConnectionCancellation { get; set; }
    public SshConnectionOptions? ActiveConnectionOptions { get; set; }
    public Guid? ActiveServerProfileId { get; set; }
    public string DisplayName { get; set; } = "新建终端";
    public int Columns { get; set; } = 80;
    public int Rows { get; set; } = 24;
    public bool TerminalReady { get; private set; }
    public bool HasConnectionHistory { get; set; }

    public void EnqueueOutput(ReadOnlySpan<byte> data) => OutputBuffer.Enqueue(data);

    public void RegisterSftpWindow(SftpWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _sftpWindows.Add(window);
        window.Closed += (_, _) => _sftpWindows.Remove(window);
    }

    public void MarkTerminalReady()
    {
        TerminalReady = true;
        _terminalReadyCompletion.TrySetResult();
    }

    public Task WaitForTerminalReadyAsync(TimeSpan timeout) =>
        _terminalReadyCompletion.Task.WaitAsync(timeout);

    public void CancelConnection()
    {
        try
        {
            ConnectionCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The connection attempt completed while the tab was being closed.
        }
    }

    private void FlushOutput(object? sender, EventArgs eventArgs)
    {
        if (!TerminalReady || WebView.CoreWebView2 is null)
        {
            return;
        }

        var batch = OutputBuffer.DequeueUpTo(MaximumOutputBatchBytes);
        if (batch.IsEmpty && batch.DroppedBytes == 0)
        {
            return;
        }

        var notice = batch.DroppedBytes == 0
            ? null
            : Encoding.UTF8.GetBytes($"\r\n[LiteTerm：终端输出过快，已丢弃 {batch.DroppedBytes:N0} 个较早字节。]\r\n");
        var payload = new byte[batch.Data.Length + (notice?.Length ?? 0)];
        var offset = 0;
        if (notice is not null)
        {
            Buffer.BlockCopy(notice, 0, payload, 0, notice.Length);
            offset = notice.Length;
        }

        Buffer.BlockCopy(batch.Data, 0, payload, offset, batch.Data.Length);
        WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "output",
            data = Convert.ToBase64String(payload)
        }));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancelConnection();
        _lifetimeCancellation.Cancel();
        _terminalReadyCompletion.TrySetCanceled();
        _outputTimer.Stop();
        try
        {
            await Task.WhenAll(_sftpWindows.ToArray().Select(window => window.CloseAndWaitAsync()));
            _sftpWindows.Clear();
            await Session.DisposeAsync();
        }
        finally
        {
            ConnectionCancellation?.Dispose();
            ConnectionCancellation = null;
            _lifetimeCancellation.Dispose();
            WebView.Dispose();
        }
    }
}
