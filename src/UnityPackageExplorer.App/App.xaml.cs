using System.Windows;

namespace UnityPackageExplorer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--bundle-menu")
        {
            try
            {
                ArchiveBundleMenuRegistration.Apply(e.Args[1], Environment.ProcessPath!);
                Shutdown(0);
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine(ex); Shutdown(2); }
            return;
        }
        if (e.Args.FirstOrDefault() == "--bundle-zips")
        {
            try
            {
                var bundle = new ArchiveBundleWindow(e.Args.Skip(1));
                MainWindow = bundle;
                bundle.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "UnityPackageをまとめる", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(2);
            }
            return;
        }
        var path = e.Args.FirstOrDefault(x => !x.StartsWith("--", StringComparison.Ordinal));
        var window = new MainWindow(path);
        MainWindow = window;
        window.Show();
    }
}
