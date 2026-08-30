using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LiteTerm.Core.Logs;
using LiteTerm.Core.QuickCommands;

namespace LiteTerm.App;

public partial class ServerLogWindow : Window
{
    private readonly Guid _serverId;
    private readonly ObservableCollection<ServerLogEntry> _entries;

    public ServerLogWindow(Guid serverId, string serverName, IReadOnlyList<ServerLogEntry> entries)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("服务器标识不能为空。", nameof(serverId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(entries);
        _serverId = serverId;
        _entries = new ObservableCollection<ServerLogEntry>(ServerLogEntry.NormalizeAll(serverId, entries));
        InitializeComponent();
        ServerNameText.Text = serverName;
        LogList.ItemsSource = _entries;
        if (_entries.Count > 0)
        {
            LogList.SelectedIndex = 0;
        }
    }

    public IReadOnlyList<ServerLogEntry> Entries => [.. _entries];

    public string? CommandToExecute { get; private set; }

    public string? RemotePathToDownload { get; private set; }

    private void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogList.SelectedItem is not ServerLogEntry entry)
        {
            return;
        }

        NameTextBox.Text = entry.Name;
        RemotePathTextBox.Text = entry.RemotePath;
        RefreshPreviews();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        LogList.SelectedItem = null;
        NameTextBox.Clear();
        RemotePathTextBox.Clear();
        FollowPreviewTextBox.Clear();
        EnterDirectoryPreviewTextBox.Clear();
        ValidationText.Text = string.Empty;
        NameTextBox.Focus();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (LogList.SelectedItem is not ServerLogEntry entry)
        {
            return;
        }

        if (MessageBox.Show(this,
                $"确定删除日志入口“{entry.Name}”吗？保存配置后生效。",
                "删除日志入口", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            != MessageBoxResult.Yes)
        {
            return;
        }

        var index = LogList.SelectedIndex;
        _entries.Remove(entry);
        LogList.SelectedIndex = Math.Min(index, _entries.Count - 1);
        if (_entries.Count == 0)
        {
            New_Click(sender, e);
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => TryApplyCurrent(showMessage: true, out _);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TryApplyPendingEdit())
        {
            DialogResult = true;
        }
    }

    private void Follow_Click(object sender, RoutedEventArgs e) =>
        CompleteWithCommand(QuickCommandDefinition.FollowLogTemplate);

    private void EnterDirectory_Click(object sender, RoutedEventArgs e) =>
        CompleteWithCommand(QuickCommandDefinition.EnterLogDirectoryTemplate);

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (!TryApplyCurrent(showMessage: true, out var entry))
        {
            return;
        }

        RemotePathToDownload = entry.RemotePath;
        DialogResult = true;
    }

    private void CompleteWithCommand(string commandTemplate)
    {
        if (!TryApplyCurrent(showMessage: true, out var entry))
        {
            return;
        }

        CommandToExecute = QuickCommandTemplate.Render(commandTemplate, entry.RemotePath);
        DialogResult = true;
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e) => RefreshPreviews();

    private void RefreshPreviews()
    {
        if (FollowPreviewTextBox is null || ValidationText is null)
        {
            return;
        }

        FollowPreviewTextBox.Clear();
        EnterDirectoryPreviewTextBox.Clear();
        ValidationText.Text = string.Empty;
        if (string.IsNullOrEmpty(RemotePathTextBox.Text))
        {
            ValidationText.Text = "填写绝对远程日志路径后将显示安全预览。";
            return;
        }

        try
        {
            var previewEntry = new ServerLogEntry(
                Guid.NewGuid(), _serverId, "预览", RemotePathTextBox.Text).Normalize();
            FollowPreviewTextBox.Text = QuickCommandTemplate.Render(
                QuickCommandDefinition.FollowLogTemplate,
                previewEntry.RemotePath);
            EnterDirectoryPreviewTextBox.Text = QuickCommandTemplate.Render(
                QuickCommandDefinition.EnterLogDirectoryTemplate,
                previewEntry.RemotePath);
        }
        catch (ArgumentException exception)
        {
            ValidationText.Text = exception.Message;
        }
    }

    private bool TryApplyPendingEdit()
    {
        if (LogList.SelectedItem is not null
            || !string.IsNullOrWhiteSpace(NameTextBox.Text)
            || !string.IsNullOrWhiteSpace(RemotePathTextBox.Text))
        {
            return TryApplyCurrent(showMessage: true, out _);
        }

        return true;
    }

    private bool TryApplyCurrent(bool showMessage, out ServerLogEntry entry)
    {
        var selected = LogList.SelectedItem as ServerLogEntry;
        try
        {
            var candidate = new ServerLogEntry(
                selected?.Id ?? Guid.NewGuid(),
                _serverId,
                NameTextBox.Text,
                RemotePathTextBox.Text).Normalize();
            if (_entries.Any(existing => existing.Id != candidate.Id
                                         && string.Equals(existing.Name, candidate.Name,
                                             StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("同一服务器的日志名称不能重复。");
            }

            if (selected is null)
            {
                if (_entries.Count >= ServerLogEntry.MaximumCountPerServer)
                {
                    throw new ArgumentException(
                        $"每台服务器最多保存 {ServerLogEntry.MaximumCountPerServer} 条日志入口。");
                }

                _entries.Add(candidate);
            }
            else
            {
                _entries[_entries.IndexOf(selected)] = candidate;
            }

            entry = candidate;
            LogList.SelectedItem = candidate;
            RefreshPreviews();
            return true;
        }
        catch (ArgumentException exception)
        {
            entry = null!;
            if (showMessage)
            {
                ValidationText.Text = exception.Message;
                MessageBox.Show(this, exception.Message, "常用日志",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return false;
        }
    }
}
