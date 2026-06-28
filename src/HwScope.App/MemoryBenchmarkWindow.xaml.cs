using System.Globalization;
using System.Windows;
using System.Windows.Input;
using HwScope.Core.Benchmark;
using HwScope.Core.Hardware;
using Wpf.Ui.Controls;

namespace HwScope.App;

public partial class MemoryBenchmarkWindow : FluentWindow
{
    private readonly IMemoryBenchmarkRunner _runner = new MemoryBenchmarkProcessRunner();
    private bool _isRunning;

    public MemoryBenchmarkWindow(HardwareReport? report)
    {
        InitializeComponent();

        Loaded += MemoryBenchmarkWindow_Loaded;

        if (report is not null)
        {
            CpuTypeText.Text = report.Processor;
            MemoryTypeText.Text = report.Memory;
            MotherboardText.Text = report.Motherboard;
        }
    }

    private void MemoryBenchmarkWindow_Loaded(object sender, RoutedEventArgs e)
    {
        App.ThemeService.Attach(this);
        App.ThemeService.StatusChanged += ThemeService_StatusChanged;

        if (!string.IsNullOrWhiteSpace(App.ThemeService.LastStatusMessage))
        {
            StatusText.Text = App.ThemeService.LastStatusMessage;
        }

        Loaded -= MemoryBenchmarkWindow_Loaded;
    }

    private void ThemeService_StatusChanged(object? sender, string status)
    {
        StatusText.Text = status;
    }

    protected override void OnClosed(EventArgs e)
    {
        App.ThemeService.StatusChanged -= ThemeService_StatusChanged;
        base.OnClosed(e);
    }

    private async void StartBenchmarkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        StartBenchmarkButton.IsEnabled = false;
        ClearBenchmarkResults();
        StatusText.Text = "正在运行内存跑分，请稍候...";
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            var progress = new Progress<MemoryBenchmarkProgress>(UpdateBenchmarkProgress);
            var result = await _runner.RunAsync(new MemoryBenchmarkOptions(), progress).ConfigureAwait(true);
            MemoryReadText.Text = FormatThroughput(result.ReadMBS);
            MemoryWriteText.Text = FormatThroughput(result.WriteMBS);
            MemoryCopyText.Text = FormatThroughput(result.CopyMBS);
            MemoryLatencyText.Text = $"{result.LatencyNs:F1} ns";
            StatusText.Text = $"完成：{result.CompletedAt:yyyy-MM-dd HH:mm:ss}，测试缓冲区 {result.SizeMiB} MiB";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"失败：{ex.Message}";
            System.Windows.MessageBox.Show(this, ex.Message, "内存跑分失败", System.Windows.MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            StartBenchmarkButton.IsEnabled = true;
            _isRunning = false;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void UpdateBenchmarkProgress(MemoryBenchmarkProgress progress)
    {
        switch (progress.Metric)
        {
            case MemoryBenchmarkMetric.Read:
                MemoryReadText.Text = FormatThroughputMiB(progress.Value);
                StatusText.Text = "Memory Read 完成，正在继续测试...";
                break;
            case MemoryBenchmarkMetric.Write:
                MemoryWriteText.Text = FormatThroughputMiB(progress.Value);
                StatusText.Text = "Memory Write 完成，正在继续测试...";
                break;
            case MemoryBenchmarkMetric.Copy:
                MemoryCopyText.Text = FormatThroughputMiB(progress.Value);
                StatusText.Text = "Memory Copy 完成，正在继续测试...";
                break;
            case MemoryBenchmarkMetric.Latency:
                MemoryLatencyText.Text = $"{progress.Value:F1} ns";
                StatusText.Text = "Memory Latency 完成，正在整理结果...";
                break;
        }
    }

    private void ClearBenchmarkResults()
    {
        MemoryReadText.Text = string.Empty;
        MemoryWriteText.Text = string.Empty;
        MemoryCopyText.Text = string.Empty;
        MemoryLatencyText.Text = string.Empty;
    }

    private static string FormatThroughput(double value)
    {
        return $"{value.ToString("F0", CultureInfo.InvariantCulture)} MB/s";
    }

    private static string FormatThroughputMiB(double value)
    {
        return FormatThroughput(value * 1024.0 * 1024.0 / 1_000_000.0);
    }
}

