using System.Diagnostics;
using System.IO;
using System.Windows;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Sftp;

namespace LiteTerm.App;

public partial class LogDownloadWindow : Window, ITabOwnedWindow
{
    private readonly ISftpSession _session;
    private readonly SshConnectionOptions _options;
    private readonly Func<HostKeyInfo, bool> _hostKeyVerifier;
    private readonly string _remotePath;
    private readonly string _localPath;
    private readonly SftpTransferConflictPolicy _conflictPolicy;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Stopwatch _transferStopwatch = new();
    private readonly TaskCompletionSource _closedCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _transferCancellation;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    public LogDownloadWindow(
        ISftpSession session,
        SshConnectionOptions options,
        Func<HostKeyInfo, bool> hostKeyVerifier,
        string remotePath,
        string localPath,
        SftpTransferConflictPolicy conflictPolicy)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        if (!Enum.IsDefined(conflictPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(conflictPolicy));
        }

        _session = session;
        _options = options;
        _hostKeyVerifier = hostKeyVerifier;
        _remotePath = RemotePath.Normalize(remotePath);
        _localPath = Path.GetFullPath(localPath);
        _conflictPolicy = conflictPolicy;

        InitializeComponent();
        RemotePathText.Text = _remotePath;
        Loaded += LogDownloadWindow_Loaded;
        Closing += LogDownloadWindow_Closing;
        Closed += (_, _) => _closedCompletion.TrySetResult();
    }

    private async void LogDownloadWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _session.ConnectAsync(_options, _hostKeyVerifier, _lifetimeCancellation.Token);
            await DownloadAsync();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // 关闭窗口或所属标签会取消连接与下载。
        }
        catch (Exception) when (!_lifetimeCancellation.IsCancellationRequested)
        {
            ShowFailure("无法建立独立 SFTP 连接。");
        }
        catch (Exception)
        {
            // 标签或窗口关闭期间不再弹出远程连接错误。
        }
    }

    private async Task DownloadAsync()
    {
        _transferCancellation?.Dispose();
        _transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        var cancellation = _transferCancellation;
        var progress = new Progress<SftpTransferProgress>(UpdateProgress);
        var remoteName = RemotePath.GetName(_remotePath);
        TransferTitleText.Text = string.IsNullOrEmpty(remoteName)
            ? "正在下载远程日志"
            : $"正在下载 {remoteName}";
        TransferProgressBar.Value = 0;
        TransferProgressText.Text = "0 B / 0 B · 0%";
        StatusText.Text = $"目标：{_localPath}";
        CancelButton.IsEnabled = true;
        _transferStopwatch.Restart();

        try
        {
            await DownloadWithConflictRetryAsync(progress, cancellation.Token);
            _transferStopwatch.Stop();
            TransferTitleText.Text = "日志下载完成";
            StatusText.Text = $"已保存到：{_localPath}";
        }
        catch (OperationCanceledException)
        {
            _transferStopwatch.Stop();
            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                TransferTitleText.Text = "日志下载已取消";
                StatusText.Text = "临时文件已清理，已有目标文件未被替换。";
            }
        }
        catch (Exception) when (!_lifetimeCancellation.IsCancellationRequested)
        {
            _transferStopwatch.Stop();
            ShowFailure("日志下载失败。");
        }
        catch (Exception)
        {
            _transferStopwatch.Stop();
            // 标签或窗口关闭期间不再弹出传输错误。
        }
        finally
        {
            CancelButton.IsEnabled = false;
            if (ReferenceEquals(_transferCancellation, cancellation))
            {
                _transferCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task DownloadWithConflictRetryAsync(
        IProgress<SftpTransferProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await _session.DownloadFileAsync(
                _remotePath,
                _localPath,
                _conflictPolicy,
                progress,
                cancellationToken);
        }
        catch (SftpTransferConflictException) when (
            _conflictPolicy == SftpTransferConflictPolicy.Fail)
        {
            if (MessageBox.Show(this,
                    $"传输期间本地文件已被创建，是否覆盖？\n\n{_localPath}",
                    "确认覆盖",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            await _session.DownloadFileAsync(
                _remotePath,
                _localPath,
                SftpTransferConflictPolicy.Overwrite,
                progress,
                cancellationToken);
        }
    }

    private void UpdateProgress(SftpTransferProgress snapshot)
    {
        TransferProgressBar.Value = snapshot.Percentage;
        var elapsedSeconds = _transferStopwatch.Elapsed.TotalSeconds;
        var bytesPerSecond = elapsedSeconds <= 0
            ? 0
            : snapshot.BytesTransferred / elapsedSeconds;
        TransferProgressText.Text =
            $"{FormatBytes(snapshot.BytesTransferred)} / {FormatBytes(snapshot.TotalBytes)} · " +
            $"{snapshot.Percentage:N0}% · {FormatBytes(bytesPerSecond)}/s";
    }

    private void ShowFailure(string message)
    {
        var safeMessage = _session.LastFailure?.UserMessage ?? message;
        TransferTitleText.Text = "日志下载失败";
        StatusText.Text = safeMessage;
        CancelButton.IsEnabled = false;
        MessageBox.Show(this,
            safeMessage,
            "下载日志",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_transferCancellation is { IsCancellationRequested: false } cancellation)
        {
            cancellation.Cancel();
        }
        else if (!_lifetimeCancellation.IsCancellationRequested)
        {
            _lifetimeCancellation.Cancel();
        }

        CancelButton.IsEnabled = false;
        StatusText.Text = "正在取消下载…";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{value:N0} {units[unitIndex]}"
            : $"{value:N1} {units[unitIndex]}";
    }

    public async Task CloseAndWaitAsync()
    {
        if (!_shutdownStarted)
        {
            Close();
        }

        await _closedCompletion.Task;
    }

    private async void LogDownloadWindow_Closing(
        object? sender,
        System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownCompleted)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        IsEnabled = false;
        _lifetimeCancellation.Cancel();
        _transferCancellation?.Cancel();
        try
        {
            await _session.DisposeAsync();
        }
        catch (Exception)
        {
            // 即使远程连接在释放时失败，也必须允许标签和应用继续关闭。
        }
        finally
        {
            _transferCancellation?.Dispose();
            _lifetimeCancellation.Dispose();
            _shutdownCompleted = true;
            _ = Dispatcher.BeginInvoke(Close);
        }
    }
}
