using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Logs;
using LiteTerm.Core.QuickCommands;
using LiteTerm.Core.Servers;
using LiteTerm.Core.Settings;
using LiteTerm.Core.Sftp;
using LiteTerm.Core.Terminal;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace LiteTerm.App;

public partial class MainWindow : Window
{
    private readonly Func<ISshTerminalSession> _sshSessionFactory;
    private readonly IServerProfileRepository _dataStore;
    private readonly IKnownHostStore _knownHostStore;
    private readonly IApplicationAppearanceSettingsStore _appearanceSettingsStore;
    private readonly IQuickCommandStore _quickCommandStore;
    private readonly IServerLogEntryStore _serverLogEntryStore;
    private readonly IConnectionDiagnosticLogger _diagnosticLogger;
    private readonly Func<ISftpSession> _sftpSessionFactory;
    private readonly List<TerminalTabContext> _terminalTabs = [];
    private readonly ObservableCollection<ServerProfile> _serverProfiles = [];
    private readonly ICollectionView _serverProfilesView;
    private ApplicationTheme _applicationTheme = ApplicationTheme.Dark;
    private TerminalAppearanceSettings _terminalAppearance = TerminalAppearanceSettings.Default;
    private bool _updatingAutoReconnectControl;
    private bool _shutdownStarted;
    private bool _shutdownCompleted;

    private TerminalTabContext? CurrentTab => TerminalTabs.SelectedItem is TabItem { Tag: TerminalTabContext tab }
        ? tab
        : null;

    public MainWindow(
        Func<ISshTerminalSession> sshSessionFactory,
        IServerProfileRepository dataStore,
        IKnownHostStore knownHostStore,
        IApplicationAppearanceSettingsStore appearanceSettingsStore,
        IQuickCommandStore quickCommandStore,
        IServerLogEntryStore serverLogEntryStore,
        IConnectionDiagnosticLogger diagnosticLogger,
        Func<ISftpSession> sftpSessionFactory)
    {
        ArgumentNullException.ThrowIfNull(sshSessionFactory);
        ArgumentNullException.ThrowIfNull(dataStore);
        ArgumentNullException.ThrowIfNull(knownHostStore);
        ArgumentNullException.ThrowIfNull(appearanceSettingsStore);
        ArgumentNullException.ThrowIfNull(quickCommandStore);
        ArgumentNullException.ThrowIfNull(serverLogEntryStore);
        ArgumentNullException.ThrowIfNull(diagnosticLogger);
        ArgumentNullException.ThrowIfNull(sftpSessionFactory);
        _sshSessionFactory = sshSessionFactory;
        _dataStore = dataStore;
        _knownHostStore = knownHostStore;
        _appearanceSettingsStore = appearanceSettingsStore;
        _quickCommandStore = quickCommandStore;
        _serverLogEntryStore = serverLogEntryStore;
        _diagnosticLogger = diagnosticLogger;
        _sftpSessionFactory = sftpSessionFactory;

        InitializeComponent();
        _serverProfilesView = CollectionViewSource.GetDefaultView(_serverProfiles);
        _serverProfilesView.Filter = item =>
            item is ServerProfile profile && profile.MatchesSearch(ServerSearchTextBox.Text);
        _serverProfilesView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(ServerProfile.GroupName), new ServerGroupNameConverter()));
        ApplyServerSort();
        ServerList.ItemsSource = _serverProfilesView;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        AddTerminalTab();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _dataStore.InitializeAsync();
            _applicationTheme = await _appearanceSettingsStore.GetApplicationThemeAsync();
            _terminalAppearance = await _appearanceSettingsStore.GetTerminalAppearanceAsync();
            ApplicationThemeManager.Apply(_applicationTheme);
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

        foreach (var tab in _terminalTabs.ToArray())
        {
            await InitializeTerminalAsync(tab);
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
        var tab = CurrentTab;
        if (tab is null || tab.HasConnectionHistory || tab.Session.State != ConnectionState.Disconnected)
        {
            tab = AddTerminalTab();
            if (!await InitializeTerminalAsync(tab))
            {
                return;
            }
        }

        await ConnectTabAsync(tab, options, savedProfile, credentialToSave);
    }

    private async Task ConnectTabAsync(
        TerminalTabContext tab,
        SshConnectionOptions options,
        ServerProfile? savedProfile = null,
        ServerCredential? credentialToSave = null,
        bool isAutomaticReconnect = false,
        CancellationToken cancellationToken = default)
    {
        if (!isAutomaticReconnect)
        {
            await tab.StopAutoReconnectAsync();
        }

        if (!_terminalTabs.Contains(tab)
            || tab.Session.State is ConnectionState.Connecting or ConnectionState.Connected
                or ConnectionState.Disconnecting)
        {
            return;
        }

        if (!tab.TerminalReady)
        {
            if (!await InitializeTerminalAsync(tab))
            {
                return;
            }
        }

        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            tab.LifetimeToken,
            cancellationToken);
        var connected = false;
        tab.ConnectionCancellation = connectionCancellation;
        tab.HasConnectionHistory = true;
        tab.ActiveServerProfileId = credentialToSave is null ? savedProfile?.Id : null;
        tab.LastConnectionOptions = options;
        tab.LastServerProfileId = credentialToSave is null ? savedProfile?.Id : null;
        tab.DisplayName = savedProfile?.Name ?? $"{options.Username}@{options.Host}";
        UpdateTabHeader(tab);
        try
        {
            if (ReferenceEquals(tab, CurrentTab))
            {
                SetConnectionControls(false, true, "取消连接");
            }
            await tab.Session.ConnectAsync(
                options,
                hostKey => VerifyHostKey(options, hostKey),
                tab.Columns,
                tab.Rows,
                connectionCancellation.Token);

            if (connectionCancellation.IsCancellationRequested || !_terminalTabs.Contains(tab))
            {
                await tab.Session.DisconnectAsync();
                return;
            }

            if (ReferenceEquals(tab, CurrentTab))
            {
                tab.WebView.Focus();
            }
            tab.ActiveConnectionOptions = options;
            tab.HasSuccessfulConnection = true;
            connected = true;
        }
        catch (OperationCanceledException) when (connectionCancellation.IsCancellationRequested)
        {
            if (!isAutomaticReconnect && ReferenceEquals(tab, CurrentTab))
            {
                SetStatus("已取消连接", "#9CA3AF");
                SetConnectionControls(true);
            }
        }
        catch (Exception)
        {
            if (_terminalTabs.Contains(tab))
            {
                if (ReferenceEquals(tab, CurrentTab))
                {
                    SetConnectionControls(true);
                }

                if (!isAutomaticReconnect)
                {
                    MessageBox.Show(this,
                        tab.Session.LastFailure?.UserMessage
                        ?? "无法建立 SSH 连接，请检查连接信息后重试。",
                        "连接失败", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(tab.ConnectionCancellation, connectionCancellation))
            {
                tab.ConnectionCancellation = null;
            }
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

                var selectedProfileId = ReferenceEquals(tab, CurrentTab)
                    ? connectedProfile.Id
                    : (ServerList.SelectedItem as ServerProfile)?.Id;
                await RefreshServerProfilesAsync(selectedProfileId);
                tab.ActiveServerProfileId = connectedProfile.Id;
                tab.LastServerProfileId = connectedProfile.Id;
                if (ReferenceEquals(tab, CurrentTab))
                {
                    SetConnectionControls(false, true);
                }
            }
            catch (Exception)
            {
                if (credentialToSave is not null)
                {
                    tab.LastServerProfileId = null;
                }

                var message = credentialToSave is null
                    ? "连接已建立，但无法更新最近连接时间。"
                    : "连接已建立，但无法保存至本地连接。";
                if (!isAutomaticReconnect)
                {
                    MessageBox.Show(this, message,
                        "服务器管理", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }

    private TerminalTabContext AddTerminalTab()
    {
        var webView = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(_terminalAppearance.BackgroundColor)
        };
        var tab = new TerminalTabContext(_sshSessionFactory(), webView, Dispatcher);
        tab.Session.OutputReceived += Session_OutputReceived;
        tab.Session.StateChanged += Session_StateChanged;
        _terminalTabs.Add(tab);

        var tabItem = new TabItem
        {
            Tag = tab,
            Content = new Border
            {
                Background = (Brush)new BrushConverter().ConvertFromString(_terminalAppearance.BackgroundColor)!,
                Padding = new Thickness(1),
                Child = webView
            }
        };
        tabItem.Header = CreateTabHeader(tab, tabItem);
        TerminalTabs.Items.Add(tabItem);
        TerminalTabs.SelectedItem = tabItem;
        return tab;
    }

    private FrameworkElement CreateTabHeader(TerminalTabContext tab, TabItem tabItem)
    {
        var title = new TextBlock
        {
            Text = tab.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 8, 0),
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var close = new Button
        {
            Content = "×",
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            ToolTip = "关闭标签"
        };
        close.Click += async (_, _) => await CloseTerminalTabAsync(tabItem, tab);
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(title);
        panel.Children.Add(close);
        return panel;
    }

    private Task<bool> InitializeTerminalAsync(TerminalTabContext tab)
    {
        if (tab.TerminalReady)
        {
            return Task.FromResult(true);
        }

        if (tab.TerminalInitializationTask is { IsCompleted: false } currentInitialization)
        {
            return currentInitialization;
        }

        var initialization = InitializeTerminalCoreAsync(tab);
        tab.TerminalInitializationTask = initialization;
        return initialization;
    }

    private async Task<bool> InitializeTerminalCoreAsync(TerminalTabContext tab)
    {
        tab.BeginTerminalInitialization();
        if (ReferenceEquals(tab, CurrentTab))
        {
            UpdateCurrentTabState(tab);
        }

        try
        {
            if (tab.WebView.CoreWebView2 is null)
            {
                await tab.WebView.EnsureCoreWebView2Async();
            }

            var coreWebView = tab.WebView.CoreWebView2
                ?? throw new InvalidOperationException("WebView2 初始化未返回核心实例。");
            coreWebView.Settings.AreDevToolsEnabled = false;
            coreWebView.Settings.IsStatusBarEnabled = false;
            coreWebView.Settings.AreBrowserAcceleratorKeysEnabled = false;
            coreWebView.WebMessageReceived -= Terminal_WebMessageReceived;
            coreWebView.NavigationStarting -= Terminal_NavigationStarting;
            coreWebView.WebMessageReceived += Terminal_WebMessageReceived;
            coreWebView.NavigationStarting += Terminal_NavigationStarting;
            var terminalPage = new Uri(Path.Combine(AppContext.BaseDirectory, "Terminal", "index.html"));
            coreWebView.Navigate(terminalPage.AbsoluteUri);
            return await tab.WaitForTerminalReadyAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) when (tab.LifetimeToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception) when (tab.LifetimeToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            await HandleTerminalInitializationFailureAsync(tab, exception);
            return false;
        }
    }

    private async Task HandleTerminalInitializationFailureAsync(TerminalTabContext tab, Exception exception)
    {
        var runtimeMissing = ContainsExceptionType(exception, "WebView2RuntimeNotFoundException");
        var failure = new TerminalInitializationFailure(exception is TimeoutException
            ? TerminalInitializationFailureKind.Timeout
            : runtimeMissing
                ? TerminalInitializationFailureKind.RuntimeMissing
                : TerminalInitializationFailureKind.Unknown);
        tab.MarkTerminalInitializationFailed(failure);

        if (ReferenceEquals(tab, CurrentTab))
        {
            UpdateCurrentTabState(tab);
        }

        try
        {
            await _diagnosticLogger.WriteAsync(new ConnectionDiagnosticEntry(
                DateTimeOffset.UtcNow,
                ConnectionProtocol.Terminal,
                ConnectionOperation.Initialize,
                failure.Code,
                exception.GetType().FullName ?? exception.GetType().Name));
        }
        catch
        {
            // 诊断日志不可用时仍需保留安全提示和重试入口。
        }

        if (!_terminalTabs.Contains(tab))
        {
            return;
        }

        MessageBox.Show(this, failure.UserMessage, "终端初始化失败",
            MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static bool ContainsExceptionType(Exception exception, string typeName)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (string.Equals(current.GetType().Name, typeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async void NewTerminalTab_Click(object sender, RoutedEventArgs e)
    {
        var tab = AddTerminalTab();
        if (IsLoaded)
        {
            await InitializeTerminalAsync(tab);
        }
    }

    private async void RetryTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTab is { } tab)
        {
            await InitializeTerminalAsync(tab);
        }
    }

    private async void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift))
            != (ModifierKeys.Control | ModifierKeys.Shift))
        {
            return;
        }

        if (e.Key == Key.T)
        {
            e.Handled = true;
            var tab = AddTerminalTab();
            await InitializeTerminalAsync(tab);
        }
        else if (e.Key == Key.W
                 && TerminalTabs.SelectedItem is TabItem { Tag: TerminalTabContext tab } tabItem)
        {
            e.Handled = true;
            await CloseTerminalTabAsync(tabItem, tab);
        }
    }

    private async Task CloseTerminalTabAsync(TabItem tabItem, TerminalTabContext tab)
    {
        if (tab.Session.State is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Disconnecting
            && MessageBox.Show(this,
                $"标签“{tab.DisplayName}”仍有活动会话。关闭标签将断开连接，是否继续？",
                "关闭终端标签", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        tab.Session.OutputReceived -= Session_OutputReceived;
        tab.Session.StateChanged -= Session_StateChanged;
        if (tab.WebView.CoreWebView2 is not null)
        {
            tab.WebView.CoreWebView2.WebMessageReceived -= Terminal_WebMessageReceived;
            tab.WebView.CoreWebView2.NavigationStarting -= Terminal_NavigationStarting;
        }
        TerminalTabs.Items.Remove(tabItem);
        _terminalTabs.Remove(tab);
        try
        {
            await tab.DisposeAsync();
        }
        catch (Exception)
        {
            MessageBox.Show(this, "终端标签关闭时部分资源释放失败。", "关闭终端标签",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (_terminalTabs.Count == 0 && IsLoaded)
        {
            var replacement = AddTerminalTab();
            await InitializeTerminalAsync(replacement);
        }
    }

    private void TerminalTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, TerminalTabs) || CurrentTab is not { } tab)
        {
            return;
        }

        SizeText.Text = $"{tab.Columns} × {tab.Rows}";
        UpdateCurrentTabState(tab);
    }

    private void UpdateCurrentTabState(TerminalTabContext tab)
    {
        switch (tab.Session.State)
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
            case ConnectionState.Failed:
                SetStatus(
                    tab.AutoReconnectTask is { IsCompleted: false } && tab.AutoReconnectAttemptCount > 0
                        ? $"正在等待自动重连（{tab.AutoReconnectAttemptCount}/{TerminalAutoReconnectPolicy.MaximumAttempts}）…"
                        : tab.Session.LastFailure?.UserMessage ?? "连接失败",
                    tab.AutoReconnectTask is { IsCompleted: false } ? "#F59E0B" : "#EF4444");
                SetConnectionControls(true);
                break;
            default:
                SetStatus(tab.TerminalInitializationState == TerminalInitializationState.Failed
                    ? tab.LastTerminalInitializationFailure?.UserMessage
                      ?? "终端组件初始化失败，请重试终端。"
                    : tab.TerminalReady
                        ? "终端已就绪"
                        : "正在初始化终端…",
                    tab.TerminalInitializationState == TerminalInitializationState.Failed ? "#EF4444" : "#9CA3AF");
                SetConnectionControls(true);
                break;
        }
    }

    private void UpdateTabHeader(TerminalTabContext tab)
    {
        var text = TerminalTabTitle.Format(tab.DisplayName, tab.Session.State, tab.HasConnectionHistory);
        var tabItem = TerminalTabs.Items.OfType<TabItem>().FirstOrDefault(item => ReferenceEquals(item.Tag, tab));
        if (tabItem?.Header is StackPanel panel && panel.Children[0] is TextBlock title)
        {
            title.Text = text;
        }
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var tab = CurrentTab;
        if (tab is null)
        {
            return;
        }

        if (tab.Session.State == ConnectionState.Connecting)
        {
            tab.CancelConnection();
            SetStatus("正在取消连接…", "#F59E0B");
            SetConnectionControls(false);
            return;
        }

        try
        {
            await tab.Session.DisconnectAsync();
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
        var tab = _terminalTabs.FirstOrDefault(candidate => ReferenceEquals(candidate.WebView.CoreWebView2, sender));
        if (tab is null)
        {
            return;
        }

        try
        {
            using var message = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            var root = message.RootElement;
            if (!root.TryGetProperty("type", out var typeElement)) return;

            switch (typeElement.GetString())
            {
                case "ready":
                    tab.MarkTerminalReady();
                    ApplyTerminalAppearance(tab);
                    if (ReferenceEquals(tab, CurrentTab))
                    {
                        UpdateCurrentTabState(tab);
                    }
                    break;
                case "input" when root.TryGetProperty("data", out var dataElement):
                    _ = SendTerminalInputAsync(tab, dataElement.GetString() ?? string.Empty);
                    break;
                case "resize" when root.TryGetProperty("columns", out var columnsElement)
                                   && root.TryGetProperty("rows", out var rowsElement):
                    tab.Columns = Math.Max(columnsElement.GetInt32(), 1);
                    tab.Rows = Math.Max(rowsElement.GetInt32(), 1);
                    if (ReferenceEquals(tab, CurrentTab))
                    {
                        SizeText.Text = $"{tab.Columns} × {tab.Rows}";
                    }
                    tab.Session.Resize(tab.Columns, tab.Rows);
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed messages from the isolated terminal page.
        }
    }

    private async Task SendTerminalInputAsync(TerminalTabContext tab, string input)
    {
        var cancellationToken = tab.LifetimeToken;
        try
        {
            await tab.Session.SendAsync(input, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Closing the owning tab cancels pending terminal writes without changing another tab's status.
        }
        catch (Exception)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_terminalTabs.Contains(tab) && ReferenceEquals(tab, CurrentTab))
                {
                    SetStatus("终端输入发送失败，连接可能已中断。", "#EF4444");
                }
            });
        }
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        var tab = CurrentTab;
        if (tab?.LastConnectionOptions is not { } options
            || !TerminalReconnectPolicy.CanReconnect(tab.Session.State, hasConnectionSnapshot: true))
        {
            MessageBox.Show(this, "当前标签没有可用于重连的连接记录。", "重新连接",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var savedProfile = tab.LastServerProfileId is { } serverId
            ? _serverProfiles.FirstOrDefault(profile => profile.Id == serverId)
            : null;
        await ConnectTabAsync(tab, options, savedProfile);
    }

    private async void AutoReconnect_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingAutoReconnectControl || CurrentTab is not { } tab)
        {
            return;
        }

        tab.AutoReconnectEnabled = AutoReconnectCheckBox.IsChecked == true;
        if (!tab.AutoReconnectEnabled)
        {
            await tab.StopAutoReconnectAsync();
            if (_terminalTabs.Contains(tab) && ReferenceEquals(tab, CurrentTab))
            {
                UpdateCurrentTabState(tab);
            }
            return;
        }

        TryStartAutoReconnect(tab);
    }

    private void Sftp_Click(object sender, RoutedEventArgs e)
    {
        var tab = CurrentTab;
        var options = tab?.ActiveConnectionOptions;
        if (tab?.Session.State != ConnectionState.Connected || options is null)
        {
            MessageBox.Show(this, "请先建立 SSH 终端连接。", "SFTP", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new SftpWindow(
            _sftpSessionFactory(),
            options,
            hostKey => VerifyHostKey(options, hostKey))
        {
            Owner = this
        };
        tab.RegisterOwnedWindow(window);
        window.Show();
    }

    private async void QuickCommands_Click(object sender, RoutedEventArgs e)
    {
        var tab = CurrentTab;
        if (tab?.Session.State != ConnectionState.Connected)
        {
            MessageBox.Show(this, "请先建立 SSH 终端连接。", "常用命令",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var commands = await _quickCommandStore.GetQuickCommandsAsync(tab.LifetimeToken);
            var dialog = new QuickCommandWindow(commands) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            await _quickCommandStore.SaveQuickCommandsAsync(dialog.Commands, tab.LifetimeToken);
            if (dialog.CommandToExecute is null)
            {
                return;
            }

            if (!_terminalTabs.Contains(tab) || tab.Session.State != ConnectionState.Connected)
            {
                MessageBox.Show(this, "当前终端连接已断开，命令未执行。", "常用命令",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await SendTerminalInputAsync(tab, dialog.CommandToExecute + "\n");
        }
        catch (OperationCanceledException) when (tab.LifetimeToken.IsCancellationRequested)
        {
            // Closing the owning tab cancels settings access and prevents sending to another tab.
        }
        catch (Exception)
        {
            MessageBox.Show(this, "无法读取或保存常用命令配置。", "常用命令",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ServerLogs_Click(object sender, RoutedEventArgs e)
    {
        var tab = CurrentTab;
        if (tab?.Session.State != ConnectionState.Connected || tab.ActiveServerProfileId is not { } serverId)
        {
            MessageBox.Show(this, "常用日志只能关联到已保存并已连接的服务器。", "常用日志",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var entries = await _serverLogEntryStore.GetForServerAsync(serverId, tab.LifetimeToken);
            var dialog = new ServerLogWindow(serverId, tab.DisplayName, entries) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            await _serverLogEntryStore.ReplaceForServerAsync(serverId, dialog.Entries, tab.LifetimeToken);
            if (dialog.RemotePathToDownload is { } remotePath)
            {
                StartLogDownload(tab, serverId, remotePath);
                return;
            }

            if (dialog.CommandToExecute is null)
            {
                return;
            }

            if (!_terminalTabs.Contains(tab)
                || tab.Session.State != ConnectionState.Connected
                || tab.ActiveServerProfileId != serverId)
            {
                MessageBox.Show(this, "当前服务器连接已断开，日志命令未执行。", "常用日志",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await SendTerminalInputAsync(tab, dialog.CommandToExecute + "\n");
        }
        catch (OperationCanceledException) when (tab.LifetimeToken.IsCancellationRequested)
        {
            // Closing the owning tab cancels settings access and prevents sending to another tab.
        }
        catch (Exception)
        {
            MessageBox.Show(this, "无法读取或保存当前服务器的常用日志。", "常用日志",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartLogDownload(TerminalTabContext tab, Guid serverId, string remotePath)
    {
        if (!_terminalTabs.Contains(tab)
            || tab.Session.State != ConnectionState.Connected
            || tab.ActiveServerProfileId != serverId
            || tab.ActiveConnectionOptions is not { } options)
        {
            MessageBox.Show(this, "当前服务器连接已断开，日志未下载。", "常用日志",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var defaultName = RemotePath.GetName(remotePath);
        var dialog = new SaveFileDialog
        {
            Title = "选择日志下载位置",
            FileName = string.IsNullOrEmpty(defaultName) ? "remote.log" : defaultName,
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
            if (MessageBox.Show(this,
                    $"本地文件已存在，是否覆盖？\n\n{dialog.FileName}",
                    "确认覆盖",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            conflictPolicy = SftpTransferConflictPolicy.Overwrite;
        }

        if (!_terminalTabs.Contains(tab)
            || tab.Session.State != ConnectionState.Connected
            || tab.ActiveServerProfileId != serverId)
        {
            MessageBox.Show(this, "当前服务器连接已断开，日志未下载。", "常用日志",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var window = new LogDownloadWindow(
            _sftpSessionFactory(),
            options,
            hostKey => VerifyHostKey(options, hostKey),
            remotePath,
            dialog.FileName,
            conflictPolicy)
        {
            Owner = this
        };
        tab.RegisterOwnedWindow(window);
        window.Show();
    }

    private async void TerminalAppearance_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new TerminalAppearanceWindow(_applicationTheme, _terminalAppearance)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _appearanceSettingsStore.SaveApplicationAppearanceAsync(dialog.SelectedApplicationTheme, dialog.Settings);
            _applicationTheme = dialog.SelectedApplicationTheme;
            _terminalAppearance = dialog.Settings;
            ApplicationThemeManager.Apply(_applicationTheme);
            UpdateTerminalHostBackground();
            foreach (var tab in _terminalTabs)
            {
                ApplyTerminalAppearance(tab);
            }
        }
        catch (Exception)
        {
            MessageBox.Show(this,
                "无法保存终端外观设置，请检查应用数据目录是否可访问。",
                "终端外观", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutWindow
        {
            Owner = this
        };
        dialog.ShowDialog();
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
                $"确定删除服务器“{profile.Name}”吗？保存的凭据和常用日志也会一并删除。",
                "删除服务器", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _dataStore.DeleteAsync(profile.Id);
            foreach (var tab in _terminalTabs.Where(tab =>
                         tab.ActiveServerProfileId == profile.Id || tab.LastServerProfileId == profile.Id))
            {
                if (tab.ActiveServerProfileId == profile.Id)
                {
                    tab.ActiveServerProfileId = null;
                }

                if (tab.LastServerProfileId == profile.Id)
                {
                    tab.LastServerProfileId = null;
                }
            }

            await RefreshServerProfilesAsync();
            if (CurrentTab is not null)
            {
                UpdateCurrentTabState(CurrentTab);
            }
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

    private void ApplyTerminalAppearance(TerminalTabContext tab)
    {
        if (!tab.TerminalReady || tab.WebView.CoreWebView2 is null)
        {
            return;
        }

        tab.WebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "appearance",
            foreground = _terminalAppearance.ForegroundColor,
            background = _terminalAppearance.BackgroundColor,
            fontFamily = _terminalAppearance.FontFamily,
            fontSize = _terminalAppearance.FontSize,
            scrollback = _terminalAppearance.Scrollback
        }));
    }

    private void UpdateTerminalHostBackground()
    {
        TerminalTabs.Background = (Brush)new BrushConverter().ConvertFromString(_terminalAppearance.BackgroundColor)!;
        foreach (var tab in _terminalTabs)
        {
            tab.WebView.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(_terminalAppearance.BackgroundColor);
            if (tab.WebView.Parent is Border border)
            {
                border.Background = TerminalTabs.Background;
            }
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
        _terminalTabs.FirstOrDefault(tab => ReferenceEquals(tab.Session, sender))?.EnqueueOutput(eventArgs.Data.Span);
    }

    private void Session_StateChanged(object? sender, ConnectionState state)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            var tab = _terminalTabs.FirstOrDefault(candidate => ReferenceEquals(candidate.Session, sender));
            if (tab is null)
            {
                return;
            }

            if (state is ConnectionState.Disconnected or ConnectionState.Failed)
            {
                tab.ActiveConnectionOptions = null;
                tab.ActiveServerProfileId = null;
            }
            UpdateTabHeader(tab);
            if (!ReferenceEquals(tab, CurrentTab))
            {
                if (state == ConnectionState.Failed)
                {
                    TryStartAutoReconnect(tab);
                }
                return;
            }

            switch (state)
            {
                case ConnectionState.Connecting:
                    SetStatus("正在连接…", "#F59E0B");
                    SetConnectionControls(false, true, "取消连接");
                    break;
                case ConnectionState.Connected:
                    SetStatus("已连接", "#22C55E");
                    SetConnectionControls(false, true);
                    SftpButton.IsEnabled = true;
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
                    SetStatus(tab.Session.LastFailure?.UserMessage ?? "连接失败", "#EF4444");
                    SetConnectionControls(true);
                    break;
            }

            if (state == ConnectionState.Failed)
            {
                TryStartAutoReconnect(tab);
            }

        });
    }

    private void TryStartAutoReconnect(TerminalTabContext tab)
    {
        if (tab.AutoReconnectTask is { IsCompleted: false }
            || !TerminalAutoReconnectPolicy.CanSchedule(
                tab.AutoReconnectEnabled,
                tab.Session.State,
                tab.LastConnectionOptions is not null,
                tab.HasSuccessfulConnection,
                tab.Session.LastFailure,
                tab.AutoReconnectAttemptCount))
        {
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(tab.LifetimeToken);
        tab.AutoReconnectCancellation = cancellation;
        tab.AutoReconnectTask = RunAutoReconnectLoopAsync(tab, cancellation);
    }

    private async Task RunAutoReconnectLoopAsync(
        TerminalTabContext tab,
        CancellationTokenSource cancellation)
    {
        try
        {
            while (TerminalAutoReconnectPolicy.CanSchedule(
                       tab.AutoReconnectEnabled,
                       tab.Session.State,
                       tab.LastConnectionOptions is not null,
                       tab.HasSuccessfulConnection,
                       tab.Session.LastFailure,
                       tab.AutoReconnectAttemptCount))
            {
                var attempt = ++tab.AutoReconnectAttemptCount;
                var delay = TerminalAutoReconnectPolicy.GetDelay(attempt);
                if (ReferenceEquals(tab, CurrentTab))
                {
                    SetStatus(
                        $"连接已中断，{delay.TotalSeconds:0} 秒后自动重连（{attempt}/{TerminalAutoReconnectPolicy.MaximumAttempts}）…",
                        "#F59E0B");
                }

                await Task.Delay(delay, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (!_terminalTabs.Contains(tab)
                    || tab.LastConnectionOptions is not { } options)
                {
                    return;
                }

                var savedProfile = tab.LastServerProfileId is { } serverId
                    ? _serverProfiles.FirstOrDefault(profile => profile.Id == serverId)
                    : null;
                tab.IsAutomaticReconnectAttempt = true;
                try
                {
                    await ConnectTabAsync(
                        tab,
                        options,
                        savedProfile,
                        isAutomaticReconnect: true,
                        cancellationToken: cancellation.Token);
                }
                finally
                {
                    tab.IsAutomaticReconnectAttempt = false;
                }

                if (tab.Session.State == ConnectionState.Connected)
                {
                    tab.AutoReconnectAttemptCount = 0;
                    return;
                }
            }

            if (_terminalTabs.Contains(tab)
                && ReferenceEquals(tab, CurrentTab)
                && tab.AutoReconnectEnabled
                && tab.Session.State == ConnectionState.Failed
                && tab.AutoReconnectAttemptCount >= TerminalAutoReconnectPolicy.MaximumAttempts)
            {
                SetStatus("自动重连已停止，请检查网络后手动重连。", "#EF4444");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The user disabled auto reconnect, started a manual reconnect, or closed the tab.
        }
        catch (Exception exception)
        {
            try
            {
                await _diagnosticLogger.WriteAsync(new ConnectionDiagnosticEntry(
                    DateTimeOffset.UtcNow,
                    ConnectionProtocol.Ssh,
                    ConnectionOperation.Connect,
                    "auto_reconnect_loop_failed",
                    exception.GetType().FullName ?? exception.GetType().Name));
            }
            catch
            {
                // A diagnostic failure must not surface as an unobserved reconnect task exception.
            }

            if (_terminalTabs.Contains(tab) && ReferenceEquals(tab, CurrentTab))
            {
                SetStatus("自动重连已停止，请手动重连。", "#EF4444");
            }
        }
        finally
        {
            if (ReferenceEquals(tab.AutoReconnectCancellation, cancellation))
            {
                tab.AutoReconnectCancellation = null;
                tab.AutoReconnectTask = null;
            }

            cancellation.Dispose();
        }
    }

    private void SetConnectionControls(bool canConnect, bool canDisconnect = false, string disconnectButtonText = "断开")
    {
        ConnectButton.IsEnabled = canConnect && CurrentTab?.TerminalReady == true;
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
        _updatingAutoReconnectControl = true;
        AutoReconnectCheckBox.IsChecked = CurrentTab?.AutoReconnectEnabled == true;
        AutoReconnectCheckBox.IsEnabled = CurrentTab?.TerminalReady == true;
        _updatingAutoReconnectControl = false;
        RetryTerminalButton.Visibility = CurrentTab?.TerminalInitializationState == TerminalInitializationState.Failed
            ? Visibility.Visible
            : Visibility.Collapsed;
        RetryTerminalButton.IsEnabled = CurrentTab?.TerminalInitializationState == TerminalInitializationState.Failed;
        ReconnectButton.IsEnabled = CurrentTab is { } currentTab
                                    && TerminalReconnectPolicy.CanReconnect(
                                        currentTab.Session.State,
                                        currentTab.LastConnectionOptions is not null);
        SftpButton.IsEnabled = CurrentTab?.Session.State == ConnectionState.Connected;
        QuickCommandButton.IsEnabled = CurrentTab?.Session.State == ConnectionState.Connected;
        LogShortcutButton.IsEnabled = CurrentTab?.Session.State == ConnectionState.Connected
                                      && CurrentTab.ActiveServerProfileId is not null;
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

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
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
        var tabs = _terminalTabs.ToArray();
        _terminalTabs.Clear();
        foreach (var tab in tabs)
        {
            tab.Session.OutputReceived -= Session_OutputReceived;
            tab.Session.StateChanged -= Session_StateChanged;
        }

        try
        {
            await Task.WhenAll(tabs.Select(tab => tab.DisposeAsync().AsTask()));
        }
        catch (Exception)
        {
            // Every tab has its own finally-based WebView cleanup; shutdown must still complete.
        }
        finally
        {
            _shutdownCompleted = true;
            // Complete the intercepted close after this asynchronous Closing callback returns.
            _ = Dispatcher.BeginInvoke(Close);
        }
    }
}
