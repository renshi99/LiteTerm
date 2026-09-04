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
    private readonly List<ITabOwnedWindow> _ownedWindows = [];
    private TaskCompletionSource _terminalReadyCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private int _disposed;

    public TerminalTabContext(
        ISshTerminalSession session,
        ILocalTerminalSession localSession,
        WebView2 webView,
        Dispatcher dispatcher)
    {
        Session = session;
        LocalSession = localSession;
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
    public ILocalTerminalSession LocalSession { get; }
    public WebView2 WebView { get; }
    public CancellationToken LifetimeToken { get; }
    public BoundedTerminalOutputBuffer OutputBuffer { get; }
    public CancellationTokenSource? ConnectionCancellation { get; set; }
    public CancellationTokenSource? AutoReconnectCancellation { get; set; }
    public Task? AutoReconnectTask { get; set; }
    public SshConnectionOptions? ActiveConnectionOptions { get; set; }
    public Guid? ActiveServerProfileId { get; set; }
    public SshConnectionOptions? LastConnectionOptions { get; set; }
    public Guid? LastServerProfileId { get; set; }
    public string DisplayName { get; set; } = "新建终端";
    public int Columns { get; set; } = 80;
    public int Rows { get; set; } = 24;
    public bool TerminalReady { get; private set; }
    public TerminalInitializationState TerminalInitializationState { get; private set; }
    public TerminalInitializationFailure? LastTerminalInitializationFailure { get; private set; }
    public Task<bool>? TerminalInitializationTask { get; set; }
    public Task? LocalTerminalStartTask { get; set; }
    public bool HasConnectionHistory { get; set; }
    public bool HasSuccessfulConnection { get; set; }
    public bool IsRemoteConnectionRequested { get; set; }
    public bool AutoReconnectEnabled { get; set; }
    public bool IsAutomaticReconnectAttempt { get; set; }
    public int AutoReconnectAttemptCount { get; set; }

    public void EnqueueOutput(ReadOnlySpan<byte> data) => OutputBuffer.Enqueue(data);

    public void RegisterOwnedWindow(ITabOwnedWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _ownedWindows.Add(window);
        window.Closed += (_, _) => _ownedWindows.Remove(window);
    }

    public void BeginTerminalInitialization()
    {
        TerminalInitializationState = TerminalInitializationState.Initializing;
        LastTerminalInitializationFailure = null;
        if (_terminalReadyCompletion.Task.IsCompleted)
        {
            _terminalReadyCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void MarkTerminalReady()
    {
        TerminalReady = true;
        TerminalInitializationState = TerminalInitializationState.Ready;
        LastTerminalInitializationFailure = null;
        _terminalReadyCompletion.TrySetResult();
    }

    public void MarkTerminalInitializationFailed(TerminalInitializationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        TerminalReady = false;
        TerminalInitializationState = TerminalInitializationState.Failed;
        LastTerminalInitializationFailure = failure;
        _terminalReadyCompletion.TrySetResult();
    }

    public async Task<bool> WaitForTerminalReadyAsync(TimeSpan timeout)
    {
        await _terminalReadyCompletion.Task.WaitAsync(timeout);
        return TerminalReady;
    }

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

    public void CancelAutoReconnect(bool resetAttemptCount = true)
    {
        try
        {
            AutoReconnectCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The reconnect loop completed while another lifecycle action was cancelling it.
        }

        if (IsAutomaticReconnectAttempt)
        {
            CancelConnection();
        }

        if (resetAttemptCount)
        {
            AutoReconnectAttemptCount = 0;
        }
    }

    public async Task StopAutoReconnectAsync(bool resetAttemptCount = true)
    {
        var reconnectTask = AutoReconnectTask;
        CancelAutoReconnect(resetAttemptCount);
        if (reconnectTask is null)
        {
            return;
        }

        try
        {
            await reconnectTask;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the expected completion path for a pending reconnect loop.
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
        await StopAutoReconnectAsync();
        _terminalReadyCompletion.TrySetCanceled();
        _outputTimer.Stop();
        try
        {
            await Task.WhenAll(_ownedWindows.ToArray().Select(window => window.CloseAndWaitAsync()));
            _ownedWindows.Clear();
            await Task.WhenAll(
                Session.DisposeAsync().AsTask(),
                LocalSession.DisposeAsync().AsTask());
        }
        finally
        {
            ConnectionCancellation?.Dispose();
            ConnectionCancellation = null;
            AutoReconnectCancellation?.Dispose();
            AutoReconnectCancellation = null;
            AutoReconnectTask = null;
            LocalTerminalStartTask = null;
            ActiveConnectionOptions = null;
            ActiveServerProfileId = null;
            LastConnectionOptions = null;
            LastServerProfileId = null;
            _lifetimeCancellation.Dispose();
            WebView.Dispose();
        }
    }
}

internal enum TerminalInitializationState
{
    NotStarted,
    Initializing,
    Ready,
    Failed
}
