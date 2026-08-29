using System.Windows;
using LiteTerm.Core.Sftp;

namespace LiteTerm.App;

public partial class RemoteNameDialog : Window
{
    public RemoteNameDialog(string title, string prompt, string initialName = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        NameTextBox.Text = initialName;
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    public string RemoteName => NameTextBox.Text;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RemotePath.ValidateName(RemoteName);
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this,
                exception.Message,
                "名称无效",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        }
    }
}
