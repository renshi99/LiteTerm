using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Servers;
using LiteTerm.Core.Settings;
using LiteTerm.Core.Terminal;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace LiteTerm.App;

public partial class MainWindow : Window
{
    private readonly ISshTerminalSession _session;
    private readonly IServerProfileRepository _dataStore;
    private readonly IKnownHostStore _knownHostStore;
    private readonly ITerminalAppearanceSettingsStore _terminalAppearanceStore;
    private const int OutputBufferCapacityBytes = 1024 * 1024;
    private const int MaximumOutputBatchBytes = 64 * 1024;
    private readonly BoundedTerminalOutputBuffer _outputBuffer = new(OutputBufferCapacityBytes);
    private readonly DispatcherTimer _outputTimer;
    private readonly ObservableCollection<ServerProfile> _serverProfiles = [];
    private readonly ICollectionView _serverProfilesView;
    private CancellationTokenSource? _connectionCancellation;
    private int _columns = 80;
    private int _rows = 24;
    private bool _terminalReady;
    private TerminalAppearanceSettings _terminalAppearance = TerminalAppearanceSettings.Default;

    public MainWindow(
        ISshTerminalSession session,
        IServerProfileRepository dataStore,
        IKnownHostStore knownHostStore,
        ITerminalAppearanceSettingsStore terminalAppearanceStore)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(dataStore);
        ArgumentNullException.ThrowIfNull(knownHostStore);
        ArgumentNullException.ThrowIfNull(terminalAppearanceStore);
        _session = session;
        _dataStore = dataStore;
        _knownHostStore = knownHostStore;
        _terminalAppearanceStore = terminalAppearanceStore;

        InitializeComponent();
        _serverProfilesView = CollectionViewSource.GetDefaultView(_serverProfiles);
        _serverProfilesView.Filter = item =>
            item is ServerProfile profile && profile.MatchesSearch(ServerSearchTextBox.Text);
        _serverProfilesView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(ServerProfile.GroupName), new ServerGroupNameConverter()));
        ApplyServerSort();
        ServerList.ItemsSource = _serverProfilesView;
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
            await _dataStore.InitializeAsync();
            _terminalAppearance = await _terminalAppearanceStore.GetTerminalAppearanceAsync();
            await RefreshServerProfilesAsync();
            UpdateTerminalHostBackground();
        }
        catch (Exception)
        {
            SetStatus("本地数据初始化失败", "#EF4444");
            MessageBox.Show(this,
                "无法初始化本地数据存储。请检查应用数据目录是否可访问。",
                "LiteTerm", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

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

        ServerProfile? profileToSave = null;
        ServerCredential? credentialToSave = null;
        if (SaveConnectionCheckBox.IsChecked == true)
        {
            profileToSave = CreateQuickConnectionProfile(options);
            credentialToSave = new ServerCredential(
                profileToSave.Id,
                authenticationType == SshAuthenticationType.Password ? options.Password : null,
                authenticationType == SshAuthenticationType.PrivateKey ? options.PrivateKeyPassphrase : null);
        }

        await ConnectAsync(options, profileToSave, credentialToSave);
    }

    private async Task ConnectAsync(
        SshConnectionOptions options,
        ServerProfile? savedProfile = null,
        ServerCredential? credentialToSave = null)
    {
        if (!_terminalReady)
        {
            MessageBox.Show(this, "终端仍在初始化，请稍后再试。", "LiteTerm", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_session.State is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Disconnecting)
        {
            MessageBox.Show(this, "请先断开当前会话。", "LiteTerm", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var connectionCancellation = new CancellationTokenSource();
        var connected = false;
        _connectionCancellation = connectionCancellation;
        try
        {
            SetConnectionControls(false, true, "取消连接");
            await _session.ConnectAsync(
                options,
                hostKey => VerifyHostKey(options, hostKey),
                _columns,
                _rows,
                connectionCancellation.Token);
            TerminalWebView.Focus();
            connected = true;
        }
        catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested)
        {
            SetStatus("已取消连接", "#9CA3AF");
            SetConnectionControls(true);
        }
        catch (Exception)
        {
            SetConnectionControls(true);
            MessageBox.Show(this,
                "无法建立 SSH 连接。请检查主机、端口、网络状态和认证信息后重试。",
                "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (ReferenceEquals(_connectionCancellation, connectionCancellation))
            {
                _connectionCancellation = null;
            }

            connectionCancellation.Dispose();
        }

        if (connected && savedProfile is not null)
        {
            try
            {
                var connectedProfile = savedProfile with { LastConnectedAt = DateTimeOffset.UtcNow };
                if (credentialToSave is null)
                {
                    await _dataStore.SaveAsync(connectedProfile);
                }
                else
                {
                    await _dataStore.SaveWithCredentialAsync(
                        connectedProfile,
                        credentialToSave with { ServerId = connectedProfile.Id });
                }

                await RefreshServerProfilesAsync(connectedProfile.Id);
            }
            catch (Exception)
            {
                var message = credentialToSave is null
                    ? "连接已建立，但无法更新最近连接时间。"
                    : "连接已建立，但无法保存至本地连接。";
                MessageBox.Show(this, message,
                    "服务器管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_session.State == ConnectionState.Connecting)
        {
            CancelConnectionAttempt();
            SetStatus("正在取消连接…", "#F59E0B");
            SetConnectionControls(false);
            return;
        }

        try
        {
            await _session.DisconnectAsync();
        }
        catch (Exception)
        {
            MessageBox.Show(this, "断开会话时发生错误。", "断开失败", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private bool VerifyHostKey(SshConnectionOptions options, HostKeyInfo hostKey)
    {
        try
        {
            var result = _knownHostStore.Verify(options.Host, options.Port, hostKey);
            return result.Status switch
            {
                KnownHostVerificationStatus.Trusted => true,
                KnownHostVerificationStatus.Mismatch => ShowHostKeyMismatch(options, hostKey, result.ExpectedHost!),
                KnownHostVerificationStatus.Unknown => ConfirmAndTrustHostKey(options, hostKey),
                _ => false
            };
        }
        catch (Exception)
        {
            Dispatcher.Invoke(() => MessageBox.Show(this,
                "无法读取或更新已知主机记录，已取消本次连接。\n\n请检查应用数据目录是否可访问。",
                "主机身份验证失败", MessageBoxButton.OK, MessageBoxImage.Error));
            return false;
        }
    }

    private bool ConfirmAndTrustHostKey(SshConnectionOptions options, HostKeyInfo hostKey)
    {
        var isTrusted = Dispatcher.Invoke(() => MessageBox.Show(this,
            $"地址：{options.Host}:{options.Port}\n算法：{hostKey.Algorithm}\n指纹：{hostKey.Sha256Fingerprint}\n\n这是首次连接此服务器。是否信任并保存其身份？",
            "确认服务器身份", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);

        if (!isTrusted)
        {
            return false;
        }

        _knownHostStore.Trust(options.Host, options.Port, hostKey);
        return true;
    }

    private bool ShowHostKeyMismatch(SshConnectionOptions options, HostKeyInfo hostKey, KnownHostEntry expectedHost)
    {
        Dispatcher.Invoke(() => MessageBox.Show(this,
            $"地址：{options.Host}:{options.Port}\n\n已保存：\n算法：{expectedHost.Algorithm}\n指纹：{expectedHost.Sha256Fingerprint}\n\n本次连接：\n算法：{hostKey.Algorithm}\n指纹：{hostKey.Sha256Fingerprint}\n\n服务器身份已变化，为保护连接安全，已取消本次连接。",
            "主机身份不匹配", MessageBoxButton.OK, MessageBoxImage.Error));
        return false;
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
                    ApplyTerminalAppearance();
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
        catch (Exception)
        {
            await Dispatcher.InvokeAsync(() => SetStatus("终端输入发送失败，连接可能已中断。", "#EF4444"));
        }
    }

    private async void TerminalAppearance_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TerminalAppearanceWindow(_terminalAppearance)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _terminalAppearanceStore.SaveTerminalAppearanceAsync(dialog.Settings);
            _terminalAppearance = dialog.Settings;
            UpdateTerminalHostBackground();
            ApplyTerminalAppearance();
        }
        catch (Exception)
        {
            MessageBox.Show(this,
                "无法保存终端外观设置，请检查应用数据目录是否可访问。",
                "终端外观", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void NewServer_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ServerProfileWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await SaveServerDialogAsync(dialog);
        }
    }

    private async void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerProfile profile)
        {
            ShowSelectServerMessage();
            return;
        }

        try
        {
            var credential = await _dataStore.GetCredentialAsync(profile.Id);
            var dialog = new ServerProfileWindow(profile, credential) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                await SaveServerDialogAsync(dialog);
            }
        }
        catch (Exception)
        {
            MessageBox.Show(this, "无法读取服务器资料或凭据。", "服务器管理", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerProfile profile)
        {
            ShowSelectServerMessage();
            return;
        }

        if (MessageBox.Show(this,
                $"确定删除服务器“{profile.Name}”吗？保存的凭据也会一并删除。",
                "删除服务器", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _dataStore.DeleteAsync(profile.Id);
            await RefreshServerProfilesAsync();
        }
        catch (Exception)
        {
            MessageBox.Show(this, "无法删除服务器资料。", "服务器管理", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CopyServer_Click(object sender, RoutedEventArgs e)
    {
        if (ServerList.SelectedItem is not ServerProfile profile)
        {
            ShowSelectServerMessage();
            return;
        }

        try
        {
            var credential = await _dataStore.GetCredentialAsync(profile.Id);
            var copiedProfile = profile.CreateCopy(
                Guid.NewGuid(),
                _serverProfiles.Select(existingProfile => existingProfile.Name),
                DateTimeOffset.UtcNow);
            var copiedCredential = (credential ?? new ServerCredential(profile.Id, null, null)) with
            {
                ServerId = copiedProfile.Id
            };

            await _dataStore.SaveWithCredentialAsync(copiedProfile, copiedCredential);
            await RefreshServerProfilesAsync(copiedProfile.Id);
        }
        catch (Exception)
        {
            MessageBox.Show(this,
                "无法复制服务器资料和受保护的凭据。",
                "服务器管理", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ConnectSavedServer_Click(object sender, RoutedEventArgs e)
    {
        await ConnectSelectedServerAsync();
    }

    private void ServerSearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_serverProfilesView is null)
        {
            return;
        }

        _serverProfilesView.Refresh();
        UpdateServerCount();
    }

    private void ServerSortComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_serverProfilesView is null)
        {
            return;
        }

        ApplyServerSort();
    }

    private void ApplyServerSort()
    {
        using (_serverProfilesView.DeferRefresh())
        {
            _serverProfilesView.SortDescriptions.Clear();
            _serverProfilesView.SortDescriptions.Add(
                new SortDescription(nameof(ServerProfile.GroupName), ListSortDirection.Ascending));

            if (ServerSortComboBox.SelectedIndex == 1)
            {
                _serverProfilesView.SortDescriptions.Add(
                    new SortDescription(nameof(ServerProfile.LastConnectedAt), ListSortDirection.Descending));
            }

            _serverProfilesView.SortDescriptions.Add(
                new SortDescription(nameof(ServerProfile.Name), ListSortDirection.Ascending));
        }
    }

    private async void ServerList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || System.Windows.Controls.ItemsControl.ContainerFromElement(ServerList, source) is not System.Windows.Controls.ListBoxItem)
        {
            return;
        }

        await ConnectSelectedServerAsync();
    }

    private async Task ConnectSelectedServerAsync()
    {
        if (ServerList.SelectedItem is not ServerProfile profile)
        {
            ShowSelectServerMessage();
            return;
        }

        try
        {
            var credential = await _dataStore.GetCredentialAsync(profile.Id);
            if (profile.AuthenticationType == SshAuthenticationType.Password && credential?.Password is null)
            {
                MessageBox.Show(this, "此服务器没有已保存的密码，请编辑服务器资料后再连接。",
                    "快速连接", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var options = new SshConnectionOptions
            {
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                AuthenticationType = profile.AuthenticationType,
                Password = profile.AuthenticationType == SshAuthenticationType.Password ? credential?.Password : null,
                PrivateKeyPath = profile.AuthenticationType == SshAuthenticationType.PrivateKey ? profile.PrivateKeyPath : null,
                PrivateKeyPassphrase = profile.AuthenticationType == SshAuthenticationType.PrivateKey
                    ? credential?.PrivateKeyPassphrase
                    : null,
                ConnectTimeout = profile.ConnectTimeout,
                KeepAliveInterval = profile.KeepAliveInterval
            };

            PopulateConnectionForm(profile, credential);
            await ConnectAsync(options, profile);
        }
        catch (Exception)
        {
            MessageBox.Show(this, "无法读取服务器资料或受保护的凭据。", "快速连接", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SaveServerDialogAsync(ServerProfileWindow dialog)
    {
        if (dialog.Profile is null || dialog.Credential is null)
        {
            return;
        }

        try
        {
            await _dataStore.SaveWithCredentialAsync(dialog.Profile, dialog.Credential);
            await RefreshServerProfilesAsync(dialog.Profile.Id);
        }
        catch (Exception)
        {
            MessageBox.Show(this,
                "无法保存服务器资料和凭据，请检查本地数据目录后重试。",
                "服务器管理", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task RefreshServerProfilesAsync(Guid? selectedId = null)
    {
        var profiles = await _dataStore.GetAllAsync();
        _serverProfiles.Clear();
        foreach (var profile in profiles)
        {
            _serverProfiles.Add(profile);
        }

        UpdateServerCount();

        if (selectedId is not null)
        {
            ServerList.SelectedItem = _serverProfiles.FirstOrDefault(profile => profile.Id == selectedId);
            if (ServerList.SelectedItem is not null)
            {
                ServerList.ScrollIntoView(ServerList.SelectedItem);
            }
        }
    }

    private void UpdateServerCount()
    {
        var visibleCount = _serverProfilesView.Cast<object>().Count();
        ServerCountText.Text = string.IsNullOrWhiteSpace(ServerSearchTextBox.Text)
            ? $"{visibleCount} 台"
            : $"{visibleCount}/{_serverProfiles.Count} 台";
    }

    private ServerProfile CreateQuickConnectionProfile(SshConnectionOptions options)
    {
        var duplicateEndpointExists = _serverProfiles.Any(profile =>
            profile.Port == options.Port
            && string.Equals(profile.Host, options.Host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(profile.Username, options.Username, StringComparison.OrdinalIgnoreCase));
        var baseName = ServerProfile.ResolveName(null, options.Username, options.Host);
        var profileName = ServerProfile.ResolveAvailableName(
            baseName,
            _serverProfiles.Select(profile => profile.Name),
            duplicateEndpointExists);
        var now = DateTimeOffset.UtcNow;

        return new ServerProfile(
            Guid.NewGuid(),
            profileName,
            null,
            options.Host.Trim(),
            options.Port,
            options.Username.Trim(),
            options.AuthenticationType,
            options.AuthenticationType == SshAuthenticationType.PrivateKey ? options.PrivateKeyPath : null,
            null,
            options.ConnectTimeout,
            options.KeepAliveInterval,
            null,
            now,
            now,
            null);
    }

    private void PopulateConnectionForm(ServerProfile profile, ServerCredential? credential)
    {
        HostTextBox.Text = profile.Host;
        PortTextBox.Text = profile.Port.ToString();
        UsernameTextBox.Text = profile.Username;
        AuthenticationComboBox.SelectedIndex = profile.AuthenticationType == SshAuthenticationType.PrivateKey ? 1 : 0;
        PasswordInput.Password = credential?.Password ?? string.Empty;
        PrivateKeyPathTextBox.Text = profile.PrivateKeyPath ?? string.Empty;
        PrivateKeyPassphraseInput.Password = credential?.PrivateKeyPassphrase ?? string.Empty;
    }

    private void ShowSelectServerMessage()
    {
        MessageBox.Show(this, "请先在左侧选择一台服务器。", "服务器管理", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ApplyTerminalAppearance()
    {
        if (!_terminalReady || TerminalWebView.CoreWebView2 is null)
        {
            return;
        }

        TerminalWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "appearance",
            foreground = _terminalAppearance.ForegroundColor,
            background = _terminalAppearance.BackgroundColor
        }));
    }

    private void UpdateTerminalHostBackground()
    {
        TerminalBorder.Background = (Brush)new BrushConverter().ConvertFromString(_terminalAppearance.BackgroundColor)!;
        TerminalWebView.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(_terminalAppearance.BackgroundColor);
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
        _outputBuffer.Enqueue(eventArgs.Data.Span);
    }

    private void FlushOutput(object? sender, EventArgs eventArgs)
    {
        if (!_terminalReady || TerminalWebView.CoreWebView2 is null) return;

        var batch = _outputBuffer.DequeueUpTo(MaximumOutputBatchBytes);
        if (batch.IsEmpty && batch.DroppedBytes == 0) return;

        var overloadNotice = batch.DroppedBytes == 0
            ? null
            : Encoding.UTF8.GetBytes($"\r\n[LiteTerm：终端输出过快，已丢弃 {batch.DroppedBytes:N0} 个较早字节。]\r\n");

        var payloadLength = batch.Data.Length + (overloadNotice?.Length ?? 0);
        var payload = new byte[payloadLength];
        var offset = 0;
        if (overloadNotice is not null)
        {
            Buffer.BlockCopy(overloadNotice, 0, payload, 0, overloadNotice.Length);
            offset = overloadNotice.Length;
        }

        Buffer.BlockCopy(batch.Data, 0, payload, offset, batch.Data.Length);

        var message = JsonSerializer.Serialize(new
        {
            type = "output",
            data = Convert.ToBase64String(payload)
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
                    SetConnectionControls(false, true, "取消连接");
                    break;
                case ConnectionState.Connected:
                    SetStatus("已连接", "#22C55E");
                    SetConnectionControls(false, true);
                    break;
                case ConnectionState.Disconnecting:
                    SetStatus("正在断开…", "#F59E0B");
                    SetConnectionControls(false);
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

    private void SetConnectionControls(bool canConnect, bool canDisconnect = false, string disconnectButtonText = "断开")
    {
        ConnectButton.IsEnabled = canConnect;
        DisconnectButton.IsEnabled = canDisconnect;
        DisconnectButton.Content = disconnectButtonText;
        HostTextBox.IsEnabled = canConnect;
        PortTextBox.IsEnabled = canConnect;
        UsernameTextBox.IsEnabled = canConnect;
        AuthenticationComboBox.IsEnabled = canConnect;
        PasswordInput.IsEnabled = canConnect;
        PrivateKeyPathTextBox.IsEnabled = canConnect;
        BrowsePrivateKeyButton.IsEnabled = canConnect;
        PrivateKeyPassphraseInput.IsEnabled = canConnect;
        SaveConnectionCheckBox.IsEnabled = canConnect;
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

    private void CancelConnectionAttempt()
    {
        try
        {
            _connectionCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The completed connection attempt has already released its cancellation source.
        }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        CancelConnectionAttempt();
        _outputTimer.Stop();
        _session.OutputReceived -= Session_OutputReceived;
        _session.StateChanged -= Session_StateChanged;
        await _session.DisposeAsync();
        TerminalWebView.Dispose();
    }
}
