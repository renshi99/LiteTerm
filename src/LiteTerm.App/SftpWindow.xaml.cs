using System.Windows;
using System.Windows.Input;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Sftp;

namespace LiteTerm.App;

public partial class SftpWindow : Window
{
    private readonly ISftpSession _session;
    private readonly SshConnectionOptions _options;
    private readonly Func<HostKeyInfo, bool> _hostKeyVerifier;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _busy;

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
        Loaded += SftpWindow_Loaded;
        Closed += SftpWindow_Closed;
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
        catch (Exception exception)
        {
            SetBusy(false, "SFTP 连接失败");
            MessageBox.Show(this,
                $"无法建立 SFTP 连接或读取初始目录。\n\n{exception.Message}",
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
        catch (Exception exception)
        {
            SetBusy(false, $"无法读取 {normalizedPath}");
            MessageBox.Show(this,
                $"无法读取远程目录。\n\n{exception.Message}",
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

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        var enabled = !busy && _session.State == ConnectionState.Connected;
        ParentButton.IsEnabled = enabled;
        PathTextBox.IsEnabled = enabled;
        GoButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
        FileGrid.IsEnabled = enabled;
        StatusText.Text = status;
    }

    private async void SftpWindow_Closed(object? sender, EventArgs e)
    {
        _lifetimeCancellation.Cancel();
        await _session.DisposeAsync();
        _lifetimeCancellation.Dispose();
    }
}
