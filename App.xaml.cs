using System.Windows;

namespace Loadout;

public partial class App : System.Windows.Application
{
    private TrayManager? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _tray = new TrayManager();
        _tray.Init();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
