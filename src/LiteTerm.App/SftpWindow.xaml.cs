using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Sftp;
using Microsoft.Win32;

namespace LiteTerm.App;

public partial class SftpWindow : Window, ITabOwnedWindow
{
    private readonly ISftpSession _session;
    private readonly SshConnectionOptions _options;
    private readonly Func<HostKeyInfo, bool> _hostKeyVerifier;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly Stopwatch _transferStopwatch = new();
    private readonly TaskCompletionSource _closedCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _transferCancellation;
    private bool _busy;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    public SftpWindow(
        ISftpSession session,
        SshConnectionOptions options,
        Func<HostKeyInfo, bool> hostKeyVerifier)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(hostKeyVerifier);

        _session = session;
        _options = options;
        _hostKeyVerifier = hostKeyVerifier;

        InitializeComponent();
        Title = $"SFTP - {options.Username}@{options.Host}";
        _session.StateChanged += Session_StateChanged;
        Loaded += SftpWindow_Loaded;
        Closing += SftpWindow_Closing;
        Closed += (_, _) =>
        {
            _session.StateChanged -= Session_StateChanged;
            _closedCompletion.TrySetResult();
        };
    }

    private async void SftpWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "正在建立独立 SFTP 连接…");
            await _session.ConnectAsync(_options, _hostKeyVerifier, _lifetimeCancellation.Token);
            PathTextBox.Text = _session.WorkingDirectory ?? "/";
            await LoadDirectoryAsync(PathTextBox.Text);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // 窗口关闭会取消尚未完成的连接或目录读取。
        }
        catch (Exception)
        {
            var message = _session.LastFailure?.UserMessage
                ?? "无法建立 SFTP 连接或读取初始目录，请检查远程路径和权限。";
            SetBusy(false, message);
            MessageBox.Show(this,
                message,
                "SFTP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadDirectoryAsync(string path)
    {
        var normalizedPath = RemotePath.Normalize(path);
        try
        {
            SetBusy(true, $"正在读取 {normalizedPath} …");
            var entries = await _session.ListDirectoryAsync(normalizedPath, _lifetimeCancellation.Token);
            PathTextBox.Text = normalizedPath;
            FileGrid.ItemsSource = entries;
            SetBusy(false, $"{entries.Count} 个项目");
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // 窗口正在关闭。
        }
        catch (Exception)
        {
            SetBusy(false, $"无法读取 {normalizedPath}");
            MessageBox.Show(this,
                _session.LastFailure?.UserMessage
                ?? "无法读取远程目录，请检查路径和远程权限。",
                "SFTP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Parent_Click(object sender, RoutedEventArgs e)
    {
        await LoadDirectoryAsync(RemotePath.GetParent(PathTextBox.Text));
    }

    private async void Go_Click(object sender, RoutedEventArgs e)
    {
        await LoadDirectoryAsync(PathTextBox.Text);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadDirectoryAsync(PathTextBox.Text);
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择要上传的文件",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await UploadFilesAsync(dialog.FileNames);
    }

    private void FileGrid_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = !_busy && GetDroppedPaths(e.Data).Count > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        StatusText.Text = e.Effects == DragDropEffects.Copy
            ? "释放鼠标以上传到当前远程目录"
            : "只能拖入本地文件，传输期间不能加入新任务";
    }

    private void FileGrid_DragLeave(object sender, DragEventArgs e)
    {
        if (!_busy)
        {
            RestoreDirectoryStatus();
        }
    }

    private async void FileGrid_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_busy)
        {
            return;
        }

        var droppedPaths = GetDroppedPaths(e.Data);
        SetBusy(true, "正在检查拖入的本地文件…");
        string[] files;
        try
        {
            files = await Task.Run(() => droppedPaths
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(), _lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        if (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        SetBusy(false, $"{files.Length} 个文件待上传");
        var ignoredCount = droppedPaths.Count - files.Length;
        if (ignoredCount > 0)
        {
            MessageBox.Show(this,
                $"已忽略 {ignoredCount} 个目录、失效路径或重复文件；拖拽上传目前只支持文件。",
                "SFTP",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        if (files.Length == 0)
        {
            RestoreDirectoryStatus();
            return;
        }

        await UploadFilesAsync(files);
    }

    private async Task UploadFilesAsync(IReadOnlyList<string> localPaths)
    {
        var uploadedCount = 0;
        foreach (var localPath in localPaths)
        {
            if (_lifetimeCancellation.IsCancellationRequested)
            {
                break;
            }

            var fileName = Path.GetFileName(localPath);
            var remotePath = RemotePath.Combine(PathTextBox.Text, fileName);
            var conflictPolicy = SftpTransferConflictPolicy.Fail;
            var existingEntry = FindEntry(remotePath);
            if (existingEntry is not null && existingEntry.Type != RemoteFileType.File)
            {
                MessageBox.Show(this,
                    $"远程路径已被非文件项目占用，已跳过此文件。\n\n{remotePath}",
                    "SFTP", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }

            if (existingEntry is not null)
            {
                if (!ConfirmOverwrite(remotePath, "远程文件已存在，是否覆盖？"))
                {
                    continue;
                }

                conflictPolicy = SftpTransferConflictPolicy.Overwrite;
            }

            var completed = await RunTransferAsync(
                $"正在上传 {fileName}",
                (progress, cancellationToken) => UploadWithConflictRetryAsync(
                    localPath,
                    remotePath,
                    conflictPolicy,
                    progress,
                    cancellationToken));
            if (!completed)
            {
                break;
            }

            uploadedCount++;
        }

        if (uploadedCount > 0 && !_lifetimeCancellation.IsCancellationRequested)
        {
            await LoadDirectoryAsync(PathTextBox.Text);
        }
        else if (!_busy)
        {
            RestoreDirectoryStatus();
        }
    }

    private static IReadOnlyList<string> GetDroppedPaths(IDataObject data)
    {
        return data.GetDataPresent(DataFormats.FileDrop, true) &&
               data.GetData(DataFormats.FileDrop, true) is string[] paths
            ? paths
            : [];
    }

    private void RestoreDirectoryStatus()
    {
        StatusText.Text = $"{FileGrid.Items.Count} 个项目";
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || FileGrid.SelectedItem is not RemoteFileEntry { Type: RemoteFileType.File } file)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "选择下载位置",
            FileName = file.Name,
            AddExtension = false,
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var conflictPolicy = SftpTransferConflictPolicy.Fail;
        if (File.Exists(dialog.FileName))
        {
            if (!ConfirmOverwrite(dialog.FileName, "本地文件已存在，是否覆盖？"))
            {
                return;
            }

            conflictPolicy = SftpTransferConflictPolicy.Overwrite;
        }

        await RunTransferAsync(
            $"正在下载 {file.Name}",
            (progress, cancellationToken) => DownloadWithConflictRetryAsync(
                file.FullPath,
                dialog.FileName,
                conflictPolicy,
                progress,
                cancellationToken));
    }

    private async void NewDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var dialog = new RemoteNameDialog("新建远程目录", "目录名称：")
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var newPath = RemotePath.Combine(PathTextBox.Text, dialog.RemoteName);
        if (FindEntry(newPath) is not null)
        {
            ShowPathConflict(newPath);
            return;
        }

        if (await RunRemoteOperationAsync(
                $"正在创建目录 {dialog.RemoteName} …",
                "无法创建远程目录。",
                cancellationToken => _session.CreateDirectoryAsync(newPath, cancellationToken)))
        {
            await LoadDirectoryAsync(PathTextBox.Text);
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || FileGrid.SelectedItem is not RemoteFileEntry entry)
        {
            return;
        }

        var dialog = new RemoteNameDialog("重命名远程项目", "新名称：", entry.Name)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.RemoteName == entry.Name)
        {
            return;
        }

        var destinationPath = RemotePath.Combine(
            RemotePath.GetParent(entry.FullPath),
            dialog.RemoteName);
        if (FindEntry(destinationPath) is not null)
        {
            ShowPathConflict(destinationPath);
            return;
        }

        if (await RunRemoteOperationAsync(
                $"正在重命名 {entry.Name} …",
                "无法重命名远程项目。",
                cancellationToken => _session.RenameAsync(
                    entry.FullPath,
                    destinationPath,
                    cancellationToken)))
        {
            await LoadDirectoryAsync(PathTextBox.Text);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || FileGrid.SelectedItem is not RemoteFileEntry entry)
        {
            return;
        }

        var description = entry.Type == RemoteFileType.Directory
            ? "将删除所选空目录。非空目录不会被递归删除。"
            : "将永久删除所选远程文件。";
        if (MessageBox.Show(this,
                $"{description}\n\n{entry.FullPath}",
                "确认删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (await RunRemoteOperationAsync(
                $"正在删除 {entry.Name} …",
                "无法删除远程项目。目录可能不是空目录，或当前账户没有足够权限。",
                cancellationToken => entry.Type == RemoteFileType.Directory
                    ? _session.DeleteDirectoryAsync(entry.FullPath, cancellationToken)
                    : _session.DeleteFileAsync(entry.FullPath, cancellationToken)))
        {
            await LoadDirectoryAsync(PathTextBox.Text);
        }
    }

    private async void PathTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _busy)
        {
            return;
        }

        e.Handled = true;
        await LoadDirectoryAsync(PathTextBox.Text);
    }

    private async void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_busy || FileGrid.SelectedItem is not RemoteFileEntry { Type: RemoteFileType.Directory } directory)
        {
            return;
        }

        await LoadDirectoryAsync(directory.FullPath);
    }

    private void FileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateActionButtons();
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        var cancellation = _transferCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        cancellation.Cancel();
        CancelTransferButton.IsEnabled = false;
        StatusText.Text = "正在取消传输…";
    }

    private async Task<bool> RunTransferAsync(
        string title,
        Func<IProgress<SftpTransferProgress>, CancellationToken, Task> transfer)
    {
        _transferCancellation?.Dispose();
        _transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        var cancellation = _transferCancellation;
        var progress = new Progress<SftpTransferProgress>(UpdateTransferProgress);

        TransferPanel.Visibility = Visibility.Visible;
        TransferTitleText.Text = title;
        TransferProgressBar.Value = 0;
        TransferProgressText.Text = "0 B / 0 B · 0%";
        CancelTransferButton.IsEnabled = true;
        _transferStopwatch.Restart();
        SetBusy(true, title);

        try
        {
            await transfer(progress, cancellation.Token);
            _transferStopwatch.Stop();
            TransferTitleText.Text = title.Replace("正在", "已", StringComparison.Ordinal);
            SetBusy(false, "传输完成");
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _transferStopwatch.Stop();
            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                TransferTitleText.Text = "传输已取消";
                SetBusy(false, "传输已取消，已有目标文件未被替换");
            }

            return false;
        }
        catch (Exception)
        {
            _transferStopwatch.Stop();
            TransferTitleText.Text = "传输失败";
            SetBusy(false, "传输失败");
            MessageBox.Show(this,
                _session.LastFailure?.UserMessage
                ?? "文件传输失败，请检查本地路径、远程权限和可用空间。",
                "SFTP", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally
        {
            CancelTransferButton.IsEnabled = false;
            if (ReferenceEquals(_transferCancellation, cancellation))
            {
                _transferCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task<bool> RunRemoteOperationAsync(
        string status,
        string errorMessage,
        Func<CancellationToken, Task> operation)
    {
        SetBusy(true, status);
        try
        {
            await operation(_lifetimeCancellation.Token);
            SetBusy(false, "操作完成");
            return true;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            return false;
        }
        catch (SftpPathConflictException exception)
        {
            SetBusy(false, "目标名称已存在");
            ShowPathConflict(exception.Path);
            return false;
        }
        catch (Exception)
        {
            SetBusy(false, "远程操作失败");
            MessageBox.Show(this,
                _session.LastFailure?.UserMessage ?? errorMessage,
                "SFTP",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }
    }

    private async Task UploadWithConflictRetryAsync(
        string localPath,
        string remotePath,
        SftpTransferConflictPolicy conflictPolicy,
        IProgress<SftpTransferProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await _session.UploadFileAsync(
                localPath,
                remotePath,
                conflictPolicy,
                progress,
                cancellationToken);
        }
        catch (SftpTransferConflictException) when (conflictPolicy == SftpTransferConflictPolicy.Fail)
        {
            if (!ConfirmOverwrite(remotePath, "传输期间远程文件已被创建，是否覆盖？"))
            {
                _transferCancellation?.Cancel();
                throw new OperationCanceledException(cancellationToken);
            }

            await _session.UploadFileAsync(
                localPath,
                remotePath,
                SftpTransferConflictPolicy.Overwrite,
                progress,
                cancellationToken);
        }
    }

    private async Task DownloadWithConflictRetryAsync(
        string remotePath,
        string localPath,
        SftpTransferConflictPolicy conflictPolicy,
        IProgress<SftpTransferProgress> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            await _session.DownloadFileAsync(
                remotePath,
                localPath,
                conflictPolicy,
                progress,
                cancellationToken);
        }
        catch (SftpTransferConflictException) when (conflictPolicy == SftpTransferConflictPolicy.Fail)
        {
            if (!ConfirmOverwrite(localPath, "传输期间本地文件已被创建，是否覆盖？"))
            {
                _transferCancellation?.Cancel();
                throw new OperationCanceledException(cancellationToken);
            }

            await _session.DownloadFileAsync(
                remotePath,
                localPath,
                SftpTransferConflictPolicy.Overwrite,
                progress,
                cancellationToken);
        }
    }

    private void UpdateTransferProgress(SftpTransferProgress snapshot)
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

    private RemoteFileEntry? FindEntry(string remotePath)
    {
        return FileGrid.ItemsSource is IEnumerable<RemoteFileEntry> entries
            ? entries.FirstOrDefault(entry => string.Equals(
                entry.FullPath,
                remotePath,
                StringComparison.Ordinal))
            : null;
    }

    private bool ConfirmOverwrite(string path, string prompt)
    {
        return MessageBox.Show(this,
            $"{prompt}\n\n{path}",
            "确认覆盖",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void ShowPathConflict(string path)
    {
        MessageBox.Show(this,
            $"目标名称已存在，请使用其他名称。\n\n{path}",
            "SFTP",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

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

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        var enabled = !busy && _session.State == ConnectionState.Connected;
        ParentButton.IsEnabled = enabled;
        PathTextBox.IsEnabled = enabled;
        GoButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
        FileGrid.IsEnabled = enabled;
        UploadButton.IsEnabled = enabled;
        NewDirectoryButton.IsEnabled = enabled;
        UpdateActionButtons();
        StatusText.Text = status;
    }

    private void Session_StateChanged(object? sender, ConnectionState state)
    {
        if (state != ConnectionState.Failed)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_shutdownStarted)
            {
                SetBusy(false, _session.LastFailure?.UserMessage ?? "SFTP 连接已中断。");
            }
        });
    }

    private void UpdateActionButtons()
    {
        var enabled = !_busy && _session.State == ConnectionState.Connected;
        var selectedEntry = FileGrid.SelectedItem as RemoteFileEntry;
        DownloadButton.IsEnabled = enabled && selectedEntry?.Type == RemoteFileType.File;
        RenameButton.IsEnabled = enabled && selectedEntry is not null;
        DeleteButton.IsEnabled = enabled && selectedEntry is not null;
    }

    public async Task CloseAndWaitAsync()
    {
        if (!_shutdownStarted)
        {
            Close();
        }

        await _closedCompletion.Task;
    }

    private async void SftpWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
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
            // Closing must complete even if the remote connection fails during disposal.
        }
        finally
        {
            _transferCancellation?.Dispose();
            _lifetimeCancellation.Dispose();
            _shutdownCompleted = true;
            // Let the current Closing callback return before requesting the final close.
            // WPF rejects a re-entrant Close while the original close is still unwinding.
            _ = Dispatcher.BeginInvoke(Close);
        }
    }
}
