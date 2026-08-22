using System.Configuration;
using System.Data;
using System.IO;
using System.Windows;
using LiteTerm.Infrastructure.Data;
using LiteTerm.Infrastructure.Security;
using LiteTerm.Infrastructure.Ssh;

namespace LiteTerm.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var applicationDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiteTerm");
        var dataStore = new SqliteServerProfileRepository(
            Path.Combine(applicationDataDirectory, "liteterm.db"),
            new WindowsDpapiSecretProtector(),
            Path.Combine(applicationDataDirectory, "known_hosts.json"));

        MainWindow = new MainWindow(new SshTerminalSession(), dataStore, dataStore);
        MainWindow.Show();
    }
}

