using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LiteTerm.Core.Connections;
using LiteTerm.Infrastructure.Ssh;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace LiteTerm.App;

public partial class MainWindow : Window
{
    private readonly ISshTerminalSession _session = new SshTerminalSession();
    private readonly ConcurrentQueue<byte[]> _outputQueue = new();
    private readonly DispatcherTimer _outputTimer;
    private int _columns = 80;
    private int _rows = 24;
    private bool _terminalReady;

    public MainWindow()
    {
        InitializeComponent();
        _session.OutputReceived += Session_OutputReceived;
        _session.StateChanged += Session_StateChanged;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;

        _outputTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, FlushOutput, Dispatcher);
        _outputTimer.Start();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await TerminalWebView.EnsureCoreWebView2Async();
            TerminalWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            TerminalWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            TerminalWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            TerminalWebView.CoreWebView2.WebMessageReceived += Terminal_WebMessageReceived;
            TerminalWebView.CoreWebView2.NavigationStarting += Terminal_NavigationStarting;

            var terminalPage = Path.Combine(AppContext.BaseDirectory, "Terminal", "index.html");
            TerminalWebView.Source = new Uri(terminalPage);
        }
        catch (Exception exception)
        {
            SetStatus($"终端初始化失败：{exception.Message}", "#EF4444");
            MessageBox.Show(this,
                "无法初始化 WebView2 终端。请确认系统已安装 WebView2 Runtime。\n\n" + exception.Message,
                "LiteTerm", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!_terminalReady)
        {
            MessageBox.Show(this, "终端仍在初始化，请稍后再试。", "LiteTerm", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!int.TryParse(PortTextBox.Text, out var port))
        {
            MessageBox.Show(this, "端口必须是数字。", "LiteTerm", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var authenticationType = GetSelectedAuthenticationType();
        var options = new SshConnectionOptions
        {
            Host = HostTextBox.Text.Trim(),
            Port = port,
            Username = UsernameTextBox.Text.Trim(),
            AuthenticationType = authenticationType,
            Password = authenticationType == SshAuthenticationType.Password ? PasswordInput.Password : null,
            PrivateKeyPath = authenticationType == SshAuthenticationType.PrivateKey
                ? PrivateKeyPathTextBox.Text.Trim()
                : null,
            PrivateKeyPassphrase = authenticationType == SshAuthenticationType.PrivateKey
                ? NullIfEmpty(PrivateKeyPassphraseInput.Password)
                : null
        };

        try
        {
            SetConnectionControls(false);
            await _session.ConnectAsync(options, VerifyHostKey, _columns, _rows);
            TerminalWebView.Focus();
        }
        catch (Exception exception)
        {
            SetConnectionControls(true);
            MessageBox.Show(this, exception.Message, "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _session.DisconnectAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "断开失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Authentication_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PasswordPanel is null || PrivateKeyPanel is null)
        {
            return;
        }

        var usePrivateKey = GetSelectedAuthenticationType() == SshAuthenticationType.PrivateKey;
        PasswordPanel.Visibility = usePrivateKey ? Visibility.Collapsed : Visibility.Visible;
        PrivateKeyPanel.Visibility = usePrivateKey ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowsePrivateKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 SSH 私钥",
            Filter = "私钥文件|*.pem;*.key;id_rsa;id_ecdsa;id_ed25519|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            PrivateKeyPathTextBox.Text = dialog.FileName;
        }
    }

    private bool VerifyHostKey(HostKeyInfo hostKey)
    {
        return Dispatcher.Invoke(() => MessageBox.Show(this,
            $"服务器提供了以下主机密钥：\n\n算法：{hostKey.Algorithm}\n指纹：{hostKey.Sha256Fingerprint}\n\n本技术验证版尚未保存指纹，因此每次连接都会确认。是否信任并继续？",
            "确认服务器身份", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
    }

    private void Terminal_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        try
        {
            using var message = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return;

            switch (typeElement.GetString())
            {
                case "ready":
                    _terminalReady = true;
                    SetStatus("终端已就绪", "#9CA3AF");
                    break;
                case "input" when root.TryGetProperty("data", out var dataElement):
                    _ = SendTerminalInputAsync(dataElement.GetString() ?? string.Empty);
                    break;
                case "resize" when root.TryGetProperty("columns", out var columnsElement)
                                   && root.TryGetProperty("rows", out var rowsElement):
                    _columns = Math.Max(columnsElement.GetInt32(), 1);
                    _rows = Math.Max(rowsElement.GetInt32(), 1);
                    SizeText.Text = $"{_columns} × {_rows}";
                    _session.Resize(_columns, _rows);
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed messages from the isolated terminal page.
        }
    }

    private async Task SendTerminalInputAsync(string input)
    {
        try
        {
            await _session.SendAsync(input);
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() => SetStatus($"发送失败：{exception.Message}", "#EF4444"));
        }
    }

    private void Terminal_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        if (!Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeFile)
        {
            eventArgs.Cancel = true;
        }
    }

    private void Session_OutputReceived(object? sender, TerminalOutputEventArgs eventArgs)
    {
        _outputQueue.Enqueue(eventArgs.Data.ToArray());
    }

    private void FlushOutput(object? sender, EventArgs eventArgs)
    {
        if (!_terminalReady || TerminalWebView.CoreWebView2 is null || _outputQueue.IsEmpty) return;

        using var buffer = new MemoryStream();
        while (buffer.Length < 64 * 1024 && _outputQueue.TryDequeue(out var chunk)) buffer.Write(chunk);
        if (buffer.Length == 0) return;

        var message = JsonSerializer.Serialize(new
        {
            type = "output",
            data = Convert.ToBase64String(buffer.ToArray())
        });
        TerminalWebView.CoreWebView2.PostWebMessageAsJson(message);
    }

    private void Session_StateChanged(object? sender, ConnectionState state)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            switch (state)
            {
                case ConnectionState.Connecting:
                    SetStatus("正在连接…", "#F59E0B");
                    SetConnectionControls(false);
                    break;
                case ConnectionState.Connected:
                    SetStatus("已连接", "#22C55E");
                    SetConnectionControls(false, true);
                    break;
                case ConnectionState.Disconnecting:
                    SetStatus("正在断开…", "#F59E0B");
                    break;
                case ConnectionState.Disconnected:
                    SetStatus("已断开", "#9CA3AF");
                    SetConnectionControls(true);
                    break;
                case ConnectionState.Failed:
                    SetStatus("连接失败", "#EF4444");
                    SetConnectionControls(true);
                    break;
            }
        });
    }

    private void SetConnectionControls(bool canConnect, bool canDisconnect = false)
    {
        ConnectButton.IsEnabled = canConnect;
        DisconnectButton.IsEnabled = canDisconnect;
        HostTextBox.IsEnabled = canConnect;
        PortTextBox.IsEnabled = canConnect;
        UsernameTextBox.IsEnabled = canConnect;
        AuthenticationComboBox.IsEnabled = canConnect;
        PasswordInput.IsEnabled = canConnect;
        PrivateKeyPathTextBox.IsEnabled = canConnect;
        BrowsePrivateKeyButton.IsEnabled = canConnect;
        PrivateKeyPassphraseInput.IsEnabled = canConnect;
    }

    private SshAuthenticationType GetSelectedAuthenticationType()
    {
        return AuthenticationComboBox.SelectedIndex == 1
            ? SshAuthenticationType.PrivateKey
            : SshAuthenticationType.Password;
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private void SetStatus(string text, string color)
    {
        StatusText.Text = text;
        StatusDot.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _outputTimer.Stop();
        _session.OutputReceived -= Session_OutputReceived;
        _session.StateChanged -= Session_StateChanged;
        await _session.DisposeAsync();
        TerminalWebView.Dispose();
    }
}
