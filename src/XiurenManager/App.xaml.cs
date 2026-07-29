using System.Windows;

namespace XiurenManager;

public partial class App : Application
{
    internal static AppState State { get; } = new();

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            State.WriteLog("界面异常: " + e.Exception);
            e.Handled = true;
        };
    }
}
