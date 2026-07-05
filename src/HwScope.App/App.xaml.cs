using System.Windows;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using HwScope.App.Configuration;
using HwScope.App.Services;
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
    public static HardwarePreloadService HardwarePreload { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var elevation = EnsureAdministrator(e.Args);
        if (elevation != ElevationResult.CurrentProcessIsElevated)
        {
            Shutdown(elevation == ElevationResult.ElevationStarted ? 0 : 1);
            return;
        }

        ThemeService = new ThemeService(
            new JsonSettingsStore(),
            new ThemeDefinitionStore(),
            new ThemeResourceBuilder());
        SingleInstanceWindows = new SingleInstanceWindowManager();
        HardwarePreload = new HardwarePreloadService();
        ThemeService.ApplyCurrentTheme();

        base.OnStartup(e);

        new HardwarePreloadWindow().Show();
    }

    private enum ElevationResult
    {
        CurrentProcessIsElevated,
        ElevationStarted,
        Failed
    }

    private static ElevationResult EnsureAdministrator(IReadOnlyList<string> args)
    {
        if (IsAdministrator())
        {
            return ElevationResult.CurrentProcessIsElevated;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "HwScope.App.exe",
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(' ', args.Select(QuoteArgument))
            });

            return ElevationResult.ElevationStarted;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            WriteCrashLog("ElevationRequired", ex);
            MessageBox.Show(
                "HwScope 需要管理员权限才能读取底层硬件信息。未获得管理员权限，程序将退出。",
                "需要管理员权限",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return ElevationResult.Failed;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string QuoteArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        return argument.Any(char.IsWhiteSpace) || argument.Contains('"')
            ? $"\"{argument.Replace("\"", "\\\"")}\""
            : argument;
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
