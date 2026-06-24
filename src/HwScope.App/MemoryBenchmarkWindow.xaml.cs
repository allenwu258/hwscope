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

        if (report is not null)
        {
            CpuTypeText.Text = report.Processor;
            MemoryTypeText.Text = report.Memory;
            MotherboardText.Text = report.Motherboard;
        }
    }

    private async void StartBenchmarkButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        StartBenchmarkButton.IsEnabled = false;
        StatusText.Text = "正在运行内存跑分，请稍候...";
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            var result = await _runner.RunAsync(new MemoryBenchmarkOptions()).ConfigureAwait(true);
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

    private static string FormatThroughput(double value)
    {
        return $"{value.ToString("F0", CultureInfo.InvariantCulture)} MB/s";
    }
}

