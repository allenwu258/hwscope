using System.Windows;
using HwScope.App.Pages;
using HwScope.Core.Hardware;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace HwScope.App;

public partial class MainWindow : FluentWindow
{
    private const string LightThemeResource = "Themes/HwScope.Light.xaml";
    private const string DarkThemeResource = "Themes/HwScope.Dark.xaml";

    private readonly HardwareSummaryPage _hardwareSummaryPage = new();
    private WindowBackdropType _backdropType = WindowBackdropType.Mica;
    private HardwareReport? _currentReport;

    public MainWindow()
    {
        InitializeComponent();

        _hardwareSummaryPage.StatusChanged += (_, status) => SetFooterStatus(status);
        _hardwareSummaryPage.CurrentReportChanged += (_, report) => _currentReport = report;

        Loaded += (_, _) => ShowHardwareSummary();
    }

    private void RootNavigation_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        if (RootNavigation.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        switch (item.Tag as string)
        {
            case "memory-benchmark":
                ShowMemoryBenchmark();
                break;
            case "summary":
                ShowHardwareSummary();
                break;
        }
    }

    private void ShowHardwareSummary_Click(object sender, RoutedEventArgs e)
    {
        ShowHardwareSummary();
    }

    private void ShowMemoryBenchmark_Click(object sender, RoutedEventArgs e)
    {
        ShowMemoryBenchmark();
    }

    private void StatusBarMenuItem_Click(object sender, RoutedEventArgs e)
    {
        RootStatusBar.Visibility = StatusBarMenuItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FollowSystemThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var systemTheme = ToApplicationTheme(ApplicationThemeManager.GetSystemTheme());
        ApplyTheme(systemTheme);
        SetThemeMenuState(isFollowSystem: true, systemTheme);
        SystemThemeWatcher.Watch(this, _backdropType, true);
    }

    private void LightThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SystemThemeWatcher.UnWatch(this);
        ApplyTheme(ApplicationTheme.Light);
        SetThemeMenuState(isFollowSystem: false, ApplicationTheme.Light);
    }

    private void DarkThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SystemThemeWatcher.UnWatch(this);
        ApplyTheme(ApplicationTheme.Dark);
        SetThemeMenuState(isFollowSystem: false, ApplicationTheme.Dark);
    }

    private void MicaBackdropMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _backdropType = MicaBackdropMenuItem.IsChecked ? WindowBackdropType.Mica : WindowBackdropType.None;
        WindowBackdropType = _backdropType;
        ApplicationThemeManager.Apply(ApplicationThemeManager.GetAppTheme(), _backdropType, true);
    }

    private void ShowHardwareSummary()
    {
        PageHost.Content = _hardwareSummaryPage;
        SetFooterStatus("硬件概览。");
    }

    private void ShowMemoryBenchmark()
    {
        if (_currentReport is null)
        {
            _hardwareSummaryPage.RefreshHardwareSummary();
            _currentReport = _hardwareSummaryPage.CurrentReport;
        }

        var window = new MemoryBenchmarkWindow(_currentReport)
        {
            Owner = this
        };
        window.Show();
        SetFooterStatus("已打开内存跑分窗口。");
    }

    private void SetFooterStatus(string text)
    {
        FooterStatusText.Text = text;
    }

    private void ApplyTheme(ApplicationTheme theme)
    {
        ApplyHwScopeThemeResources(theme);
        ApplicationThemeManager.Apply(theme, _backdropType, true);
    }

    private void SetThemeMenuState(bool isFollowSystem, ApplicationTheme theme)
    {
        FollowSystemThemeMenuItem.IsChecked = isFollowSystem;
        LightThemeMenuItem.IsChecked = !isFollowSystem && theme == ApplicationTheme.Light;
        DarkThemeMenuItem.IsChecked = !isFollowSystem && theme == ApplicationTheme.Dark;
    }

    private static ApplicationTheme ToApplicationTheme(SystemTheme theme)
    {
        return theme switch
        {
            SystemTheme.Dark => ApplicationTheme.Dark,
            SystemTheme.HCWhite or SystemTheme.HCBlack or SystemTheme.HC1 or SystemTheme.HC2 => ApplicationTheme.HighContrast,
            _ => ApplicationTheme.Light
        };
    }

    private static void ApplyHwScopeThemeResources(ApplicationTheme theme)
    {
        var source = theme == ApplicationTheme.Dark ? DarkThemeResource : LightThemeResource;
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var currentTheme = dictionaries.FirstOrDefault(IsHwScopeThemeDictionary);

        if (currentTheme is not null)
        {
            dictionaries.Remove(currentTheme);
        }

        dictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(source, UriKind.Relative)
        });
    }

    private static bool IsHwScopeThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        return source is not null
            && (source.EndsWith("HwScope.Colors.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("HwScope.Light.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("HwScope.Dark.xaml", StringComparison.OrdinalIgnoreCase));
    }
}
