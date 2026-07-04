using System.Windows;
using System.IO;
using HwScope.App.Configuration;
using HwScope.App.Theming;
using HwScope.App.Windows;

namespace HwScope.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(Path.GetTempPath(), "HwScope-crash.log");

    public static ThemeService ThemeService { get; private set; } = null!;
    public static SingleInstanceWindowManager SingleInstanceWindows { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        ThemeService = new ThemeService(
            new JsonSettingsStore(),
            new ThemeDefinitionStore(),
            new ThemeResourceBuilder());
        SingleInstanceWindows = new SingleInstanceWindowManager();
        ThemeService.ApplyCurrentTheme();

        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("DispatcherUnhandledException", e.Exception);
    }

    private static void OnCurrentDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteCrashLog("UnhandledException", exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteCrashLog("UnobservedTaskException", e.Exception);
    }

    private static void WriteCrashLog(string source, Exception exception)
    {
        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}";
        File.AppendAllText(CrashLogPath, text);
    }
}
