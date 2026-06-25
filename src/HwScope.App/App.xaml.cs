using System.Windows;
using HwScope.App.Configuration;
using HwScope.App.Theming;

namespace HwScope.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public static ThemeService ThemeService { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        ThemeService = new ThemeService(
            new JsonSettingsStore(),
            new ThemeDefinitionStore(),
            new ThemeResourceBuilder());
        ThemeService.ApplyCurrentTheme();

        base.OnStartup(e);
    }
}
