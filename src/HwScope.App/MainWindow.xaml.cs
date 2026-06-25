using System.Windows;
using HwScope.App.Theming;
using HwScope.App.Pages;
using HwScope.Core.Hardware;
using Wpf.Ui.Controls;

namespace HwScope.App;

public partial class MainWindow : FluentWindow
{
    private readonly HardwareSummaryPage _hardwareSummaryPage = new();
    private HardwareReport? _currentReport;

    public MainWindow()
    {
        InitializeComponent();

        _hardwareSummaryPage.StatusChanged += (_, status) => SetFooterStatus(status);
        _hardwareSummaryPage.CurrentReportChanged += (_, report) => _currentReport = report;
        App.ThemeService.StatusChanged += (_, status) => SetFooterStatus(status);

        Loaded += (_, _) =>
        {
            App.ThemeService.Attach(this);
            ApplyConfiguredWindowState();
            SyncThemeMenuState();
            ShowHardwareSummary();

            if (!string.IsNullOrWhiteSpace(App.ThemeService.LastStatusMessage))
            {
                SetFooterStatus(App.ThemeService.LastStatusMessage);
            }
        };
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
        App.ThemeService.SetShowStatusBar(StatusBarMenuItem.IsChecked);
    }

    private void FollowSystemThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        App.ThemeService.SetThemeMode(ThemeMode.System);
        SyncThemeMenuState();
    }

    private void LightThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        App.ThemeService.SetThemeMode(ThemeMode.Light);
        SyncThemeMenuState();
    }

    private void DarkThemeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        App.ThemeService.SetThemeMode(ThemeMode.Dark);
        SyncThemeMenuState();
    }

    private void MicaBackdropMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var backdrop = MicaBackdropMenuItem.IsChecked ? BackdropMode.Mica : BackdropMode.None;
        App.ThemeService.SetBackdrop(backdrop);
        SyncThemeMenuState();
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

    private void ApplyConfiguredWindowState()
    {
        StatusBarMenuItem.IsChecked = App.ThemeService.WindowSettings.ShowStatusBar;
        RootStatusBar.Visibility = StatusBarMenuItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncThemeMenuState()
    {
        var themeSettings = App.ThemeService.ThemeSettings;

        FollowSystemThemeMenuItem.IsChecked = themeSettings.Mode == ThemeMode.System;
        LightThemeMenuItem.IsChecked = themeSettings.Mode == ThemeMode.Light;
        DarkThemeMenuItem.IsChecked = themeSettings.Mode == ThemeMode.Dark;
        MicaBackdropMenuItem.IsChecked = themeSettings.Backdrop == BackdropMode.Mica;
    }
}
