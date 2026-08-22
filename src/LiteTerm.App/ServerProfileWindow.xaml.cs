using System.Windows;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Servers;
using Microsoft.Win32;

namespace LiteTerm.App;

public partial class ServerProfileWindow : Window
{
    private readonly ServerProfile? _existingProfile;

    public ServerProfileWindow(ServerProfile? profile = null, ServerCredential? credential = null)
    {
        _existingProfile = profile;
        InitializeComponent();

        if (profile is null)
        {
            return;
        }

        Title = $"编辑服务器 - {profile.Name}";
        NameTextBox.Text = profile.Name;
        GroupTextBox.Text = profile.GroupName;
        HostTextBox.Text = profile.Host;
        PortTextBox.Text = profile.Port.ToString();
        UsernameTextBox.Text = profile.Username;
        AuthenticationComboBox.SelectedIndex = profile.AuthenticationType == SshAuthenticationType.PrivateKey ? 1 : 0;
        PrivateKeyPathTextBox.Text = profile.PrivateKeyPath;
        PasswordInput.Password = credential?.Password ?? string.Empty;
        PrivateKeyPassphraseInput.Password = credential?.PrivateKeyPassphrase ?? string.Empty;
        DefaultRemotePathTextBox.Text = profile.DefaultRemotePath;
        ConnectTimeoutTextBox.Text = profile.ConnectTimeout.TotalSeconds.ToString("0.###");
        KeepAliveTextBox.Text = profile.KeepAliveInterval.TotalSeconds.ToString("0.###");
        RemarkTextBox.Text = profile.Remark;
    }

    public ServerProfile? Profile { get; private set; }

    public ServerCredential? Credential { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortTextBox.Text, out var port) || port is < 1 or > 65535)
        {
            ShowValidationError("端口必须是 1 到 65535 之间的整数。", PortTextBox);
            return;
        }

        if (!double.TryParse(ConnectTimeoutTextBox.Text, out var connectTimeoutSeconds)
            || !double.IsFinite(connectTimeoutSeconds)
            || connectTimeoutSeconds is <= 0 or > 86400)
        {
            ShowValidationError("连接超时必须是大于 0 且不超过 86400 的秒数。", ConnectTimeoutTextBox);
            return;
        }

        if (!double.TryParse(KeepAliveTextBox.Text, out var keepAliveSeconds)
            || !double.IsFinite(keepAliveSeconds)
            || keepAliveSeconds is < 0 or > 86400)
        {
            ShowValidationError("KeepAlive 必须是 0 到 86400 之间的秒数。", KeepAliveTextBox);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var authenticationType = GetAuthenticationType();
        var profile = new ServerProfile(
            _existingProfile?.Id ?? Guid.NewGuid(),
            ServerProfile.ResolveName(NameTextBox.Text, UsernameTextBox.Text, HostTextBox.Text),
            NullIfWhiteSpace(GroupTextBox.Text),
            HostTextBox.Text.Trim(),
            port,
            UsernameTextBox.Text.Trim(),
            authenticationType,
            authenticationType == SshAuthenticationType.PrivateKey ? NullIfWhiteSpace(PrivateKeyPathTextBox.Text) : null,
            NullIfWhiteSpace(DefaultRemotePathTextBox.Text),
            TimeSpan.FromSeconds(connectTimeoutSeconds),
            TimeSpan.FromSeconds(keepAliveSeconds),
            NullIfWhiteSpace(RemarkTextBox.Text),
            _existingProfile?.CreatedAt ?? now,
            now,
            _existingProfile?.LastConnectedAt);

        try
        {
            profile.Validate();
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, "服务器资料", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Profile = profile;
        Credential = new ServerCredential(
            profile.Id,
            authenticationType == SshAuthenticationType.Password ? PasswordInput.Password : null,
            authenticationType == SshAuthenticationType.PrivateKey
                ? NullIfEmpty(PrivateKeyPassphraseInput.Password)
                : null);
        DialogResult = true;
    }

    private void Authentication_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PasswordLabel is null)
        {
            return;
        }

        var usePrivateKey = GetAuthenticationType() == SshAuthenticationType.PrivateKey;
        PasswordLabel.Visibility = usePrivateKey ? Visibility.Collapsed : Visibility.Visible;
        PasswordInput.Visibility = usePrivateKey ? Visibility.Collapsed : Visibility.Visible;
        PrivateKeyLabel.Visibility = usePrivateKey ? Visibility.Visible : Visibility.Collapsed;
        PrivateKeyPanel.Visibility = usePrivateKey ? Visibility.Visible : Visibility.Collapsed;
        PassphraseLabel.Visibility = usePrivateKey ? Visibility.Visible : Visibility.Collapsed;
        PrivateKeyPassphraseInput.Visibility = usePrivateKey ? Visibility.Visible : Visibility.Collapsed;
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

    private SshAuthenticationType GetAuthenticationType() =>
        AuthenticationComboBox.SelectedIndex == 1 ? SshAuthenticationType.PrivateKey : SshAuthenticationType.Password;

    private void ShowValidationError(string message, System.Windows.Controls.Control control)
    {
        MessageBox.Show(this, message, "服务器资料", MessageBoxButton.OK, MessageBoxImage.Warning);
        control.Focus();
    }

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}
