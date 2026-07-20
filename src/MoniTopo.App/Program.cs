using MoniTopo.Windows.Startup;
using Velopack;

namespace MoniTopo.App;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ =>
                new CurrentUserRunRegistry().Delete(RunAtLoginService.ValueName))
            .Run();

        var application = new App();
        application.InitializeComponent();
        application.Run();
    }
}
