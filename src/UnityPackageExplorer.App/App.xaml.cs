using System.Windows;

namespace UnityPackageExplorer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var path = e.Args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal));
        var window = new MainWindow(path);
        MainWindow = window;
        window.Show();
    }
}
