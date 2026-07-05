using System.Diagnostics;
using System.Reflection;
using System.Windows;
using HwScope.App.Services;
using Wpf.Ui.Controls;

namespace HwScope.App.Windows;

public partial class HardwarePreloadWindow : FluentWindow
{
    private static readonly TimeSpan MinimumVisibleDuration = TimeSpan.FromMilliseconds(650);
    private readonly Stopwatch _visibleTimer = new();
    private CancellationTokenSource? _preloadCancellation;
    private bool _isOpeningMainWindow;
    private bool _isClosed;

    public HardwarePreloadWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {GetVersion()}";
        Loaded += HardwarePreloadWindow_Loaded;
        Closing += HardwarePreloadWindow_Closing;
        Closed += HardwarePreloadWindow_Closed;
        App.HardwarePreload.ProgressChanged += HardwarePreload_ProgressChanged;
    }

    private async void HardwarePreloadWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= HardwarePreloadWindow_Loaded;
        App.ThemeService.Attach(this);
        await StartPreloadAsync();
    }

    private async Task StartPreloadAsync()
    {
        _preloadCancellation?.Cancel();
        _preloadCancellation?.Dispose();
        _preloadCancellation = new CancellationTokenSource();
        var cancellationToken = _preloadCancellation.Token;
        _visibleTimer.Restart();
        ResetLoadingUi();

        try
        {
            await App.HardwarePreload.RefreshAsync(cancellationToken).ConfigureAwait(true);
            await EnsureMinimumVisibleDurationAsync(cancellationToken).ConfigureAwait(true);
            if (_isClosed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            OpenMainWindow();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                ShowFailure(ex);
            }
        }
    }

    private void HardwarePreload_ProgressChanged(object? sender, HardwarePreloadProgress progress)
    {
        StatusText.Text = progress.Message;

        if (progress.CompletedSteps is { } completed && progress.TotalSteps is { } total && total > 0)
        {
            var value = Math.Clamp(completed * 100.0 / total, 0, 100);
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = value;
            PercentText.Text = $"{completed}/{total}";
            StepText.Text = progress.StepName is null
                ? "CPU · Memory · Topology · Devices"
                : $"{FormatStepName(progress.StepName)} · {progress.ItemCount.GetValueOrDefault()} 项";
        }

        if (progress.State == HardwarePreloadState.Ready)
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 100;
            PercentText.Text = "完成";
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        await StartPreloadAsync();
    }

    private void ContinueButton_Click(object sender, RoutedEventArgs e)
    {
        OpenMainWindow();
    }

    private void OpenMainWindow()
    {
        if (_isOpeningMainWindow || _isClosed)
        {
            return;
        }

        _isOpeningMainWindow = true;
        DetachEvents();
        var mainWindow = new MainWindow();
        Application.Current.MainWindow = mainWindow;
        mainWindow.Show();
        Close();
    }

    private void ShowFailure(Exception exception)
    {
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 0;
        StatusText.Text = "硬件信息预加载失败";
        StepText.Text = "可以继续进入应用后重试。";
        PercentText.Text = "";
        FailureText.Text = exception.Message;
        FailureText.Visibility = Visibility.Visible;
        RetryButton.Visibility = Visibility.Visible;
        ContinueButton.Visibility = Visibility.Visible;
    }

    private void ResetLoadingUi()
    {
        StatusText.Text = "正在预加载硬件信息...";
        StepText.Text = "CPU · Memory · Topology · Devices";
        PercentText.Text = "";
        FailureText.Visibility = Visibility.Collapsed;
        RetryButton.Visibility = Visibility.Collapsed;
        ContinueButton.Visibility = Visibility.Collapsed;
        ProgressBar.Value = 0;
        ProgressBar.IsIndeterminate = true;
    }

    private async Task EnsureMinimumVisibleDurationAsync(CancellationToken cancellationToken)
    {
        var remaining = MinimumVisibleDuration - _visibleTimer.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken).ConfigureAwait(true);
        }
    }

    private void HardwarePreloadWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _isClosed = true;
        _preloadCancellation?.Cancel();
        DetachEvents();
    }

    private void HardwarePreloadWindow_Closed(object? sender, EventArgs e)
    {
        _preloadCancellation?.Dispose();
        _preloadCancellation = null;
        DetachEvents();
    }

    private void DetachEvents()
    {
        App.HardwarePreload.ProgressChanged -= HardwarePreload_ProgressChanged;
    }

    private static string FormatStepName(string stepName)
    {
        return stepName switch
        {
            "processors" => "处理器",
            "baseboard" => "主板",
            "bios" => "BIOS",
            "memory" => "内存模块",
            "video" => "显示适配器",
            "monitors" => "显示器",
            "disks" => "存储设备",
            "audio" => "音频设备",
            "network" => "网络适配器",
            "cpu-performance" => "处理器频率",
            "cpu-topology" => "处理器拓扑",
            _ => stepName
        };
    }

    private static string GetVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
    }
}
