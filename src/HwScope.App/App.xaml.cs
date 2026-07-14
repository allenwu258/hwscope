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
    public static StorageDetailService StorageDetails { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var elevation = EnsureAdministrator(e.Args);
        if (elevation == ElevationResult.ElevationStarted)
        {
            Shutdown(0);
            return;
        }

        ThemeService = new ThemeService(
            new JsonSettingsStore(),
            new ThemeDefinitionStore(),
            new ThemeResourceBuilder());
        SingleInstanceWindows = new SingleInstanceWindowManager();
        HardwarePreload = new HardwarePreloadService();
        StorageDetails = new StorageDetailService(HardwarePreload);
        ThemeService.ApplyCurrentTheme();

        base.OnStartup(e);

        new HardwarePreloadWindow().Show();
    }

    private enum ElevationResult
    {
        CurrentProcessIsElevated,
        ElevationStarted,
        ContinueWithoutElevation
    }

    private static ElevationResult EnsureAdministrator(IReadOnlyList<string> args)
    {
        if (Environment.GetEnvironmentVariable("HWSCOPE_SKIP_ELEVATION") == "1")
        {
            return ElevationResult.ContinueWithoutElevation;
        }

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
            if (process is null)
            {
                throw new InvalidOperationException("未能启动管理员权限进程。");
            }

            return ElevationResult.ElevationStarted;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            WriteCrashLog("ElevationUnavailable", ex);
            MessageBox.Show(
                "未获得管理员权限。HwScope 将继续以普通权限运行，但部分底层硬件信息可能缺失或不可用。",
                "部分硬件信息可能缺失",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return ElevationResult.ContinueWithoutElevation;
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
