using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using LiteTerm.Core.QuickCommands;

namespace LiteTerm.App;

public partial class QuickCommandWindow : Window
{
    private readonly ObservableCollection<QuickCommandDefinition> _commands;

    public QuickCommandWindow(IReadOnlyList<QuickCommandDefinition> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = new ObservableCollection<QuickCommandDefinition>(
            QuickCommandDefinition.NormalizeAll(commands));
        InitializeComponent();
        CommandList.ItemsSource = _commands;
        if (_commands.Count > 0)
        {
            CommandList.SelectedIndex = 0;
        }
    }

    public IReadOnlyList<QuickCommandDefinition> Commands => [.. _commands];

    public string? CommandToExecute { get; private set; }

    private void CommandList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CommandList.SelectedItem is not QuickCommandDefinition command)
        {
            return;
        }

        NameTextBox.Text = command.Name;
        TemplateTextBox.Text = command.CommandTemplate;
        RefreshPreview();
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        CommandList.SelectedItem = null;
        NameTextBox.Clear();
        TemplateTextBox.Clear();
        ValidationText.Text = string.Empty;
        PreviewTextBox.Clear();
        NameTextBox.Focus();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (CommandList.SelectedItem is not QuickCommandDefinition command)
        {
            return;
        }

        var index = CommandList.SelectedIndex;
        _commands.Remove(command);
        CommandList.SelectedIndex = Math.Min(index, _commands.Count - 1);
        if (_commands.Count == 0)
        {
            New_Click(sender, e);
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        TryApplyCurrent(showMessage: true, out _);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryApplyPendingEdit())
        {
            return;
        }

        DialogResult = true;
    }

    private void Execute_Click(object sender, RoutedEventArgs e)
    {
        if (!TryApplyCurrent(showMessage: true, out var command))
        {
            return;
        }

        try
        {
            CommandToExecute = QuickCommandTemplate.Render(command.CommandTemplate, RemotePathTextBox.Text);
            DialogResult = true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            ShowValidationMessage(exception.Message);
        }
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e) => RefreshPreview();

    private void RefreshPreview()
    {
        if (PreviewTextBox is null || ValidationText is null)
        {
            return;
        }

        PreviewTextBox.Clear();
        ValidationText.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(TemplateTextBox.Text))
        {
            return;
        }

        try
        {
            if (QuickCommandTemplate.RequiresPath(TemplateTextBox.Text)
                && string.IsNullOrEmpty(RemotePathTextBox.Text))
            {
                ValidationText.Text = "填写远程路径后将显示完整预览。";
                return;
            }

            PreviewTextBox.Text = QuickCommandTemplate.Render(TemplateTextBox.Text, RemotePathTextBox.Text);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            ValidationText.Text = exception.Message;
        }
    }

    private bool TryApplyPendingEdit()
    {
        if (CommandList.SelectedItem is not null
            || !string.IsNullOrWhiteSpace(NameTextBox.Text)
            || !string.IsNullOrWhiteSpace(TemplateTextBox.Text))
        {
            return TryApplyCurrent(showMessage: true, out _);
        }

        return true;
    }

    private bool TryApplyCurrent(bool showMessage, out QuickCommandDefinition command)
    {
        var selected = CommandList.SelectedItem as QuickCommandDefinition;
        try
        {
            var candidate = new QuickCommandDefinition(
                selected?.Id ?? Guid.NewGuid(),
                NameTextBox.Text,
                TemplateTextBox.Text).Normalize();

            if (_commands.Any(existing => existing.Id != candidate.Id
                                          && string.Equals(existing.Name, candidate.Name,
                                              StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("常用命令名称不能重复。");
            }

            if (selected is null)
            {
                if (_commands.Count >= QuickCommandDefinition.MaximumCount)
                {
                    throw new ArgumentException($"常用命令最多保存 {QuickCommandDefinition.MaximumCount} 条。");
                }

                _commands.Add(candidate);
            }
            else
            {
                _commands[_commands.IndexOf(selected)] = candidate;
            }

            command = candidate;
            CommandList.SelectedItem = candidate;
            RefreshPreview();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            command = null!;
            if (showMessage)
            {
                ShowValidationMessage(exception.Message);
            }

            return false;
        }
    }

    private void ShowValidationMessage(string message)
    {
        ValidationText.Text = message;
        MessageBox.Show(this, message, "常用命令", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
