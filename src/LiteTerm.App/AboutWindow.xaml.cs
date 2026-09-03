using System.Reflection;
using System.Windows;

namespace LiteTerm.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AboutWindow).Assembly;
        VersionTextBlock.Text = $"版本 {GetDisplayVersion(assembly)}";
    }

    private static string GetDisplayVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly.GetName().Version?.ToString(3)
        ?? "未知";

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
