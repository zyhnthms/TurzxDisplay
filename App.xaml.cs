using Microsoft.UI.Xaml;
using TurzxDisplay.Services;

namespace TurzxDisplay;

public partial class App : Application
{
    public static MainWindow? Main { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (s, e) =>
        {
            Log.Write($"UnhandledException: {e.Exception}");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            Log.Write($"AppDomain UnhandledException: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (s, e) =>
            Log.Write($"UnobservedTaskException: {e.Exception}");
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Log.Write("app launched");
        Main = new MainWindow();
        Main.Activate();
    }
}
