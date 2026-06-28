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
    private MemoryBenchmarkResult? _lastResult;
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
        DiagnosticsButton.IsEnabled = false;
        _lastResult = null;
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
            StatusText.Text = $"完成：{result.CompletedAt:yyyy-MM-dd HH:mm:ss}，测试缓冲区 {result.SizeMiB} MiB，{FormatQualitySummary(result)}";
            _lastResult = result;
            DiagnosticsButton.IsEnabled = true;
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

    private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null)
        {
            return;
        }

        var window = new MemoryBenchmarkDiagnosticsWindow(BuildDiagnosticsText(_lastResult))
        {
            Owner = this
        };
        window.ShowDialog();
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

    private static string FormatQualitySummary(MemoryBenchmarkResult result)
    {
        var quality = result.Quality;
        var sampleCount = result.Metrics?.Read.Samples.Count;
        var elapsed = result.ElapsedMs is { } elapsedMs ? $"，耗时 {elapsedMs / 1000.0:F1}s" : string.Empty;
        var samples = sampleCount is > 0 ? $"，样本 {sampleCount}" : string.Empty;
        if (quality is null || quality.Flags.Count == 0)
        {
            return $"结果稳定{samples}{elapsed}";
        }

        var label = quality.HighVariance || quality.BackgroundNoiseSuspected ? "波动较大" : "需留意";
        return $"{label}{samples}{elapsed}";
    }

    private static string BuildDiagnosticsText(MemoryBenchmarkResult result)
    {
        var lines = new List<string>
        {
            "HwScope Memory Benchmark Diagnostics",
            "====================================",
            string.Empty,
            "Summary",
            $"CompletedAt       : {result.CompletedAt:yyyy-MM-dd HH:mm:ss zzz}",
            $"Read              : {result.ReadMBS:F0} MB/s ({result.ReadMiBS:F2} MiB/s)",
            $"Write             : {result.WriteMBS:F0} MB/s ({result.WriteMiBS:F2} MiB/s)",
            $"Copy              : {result.CopyMBS:F0} MB/s ({result.CopyMiBS:F2} MiB/s)",
            $"Latency           : {result.LatencyNs:F2} ns",
            $"Elapsed           : {FormatNullable(result.ElapsedMs, " ms")}",
            $"Executable        : {result.ExecutablePath ?? string.Empty}",
            string.Empty,
            "Worker",
            $"WorkerVersion     : {result.WorkerVersion ?? string.Empty}",
            $"ProtocolVersion   : {result.ProtocolVersion?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}",
            string.Empty,
            "Options"
        };

        if (result.Options is { } options)
        {
            lines.Add($"SizeMiB           : {options.SizeMiB}");
            lines.Add($"Iterations        : {options.Iterations}");
            lines.Add($"LatencySteps      : {options.LatencySteps}");
            lines.Add($"Threads           : {options.Threads}");
            lines.Add($"WorkingSetKind    : {options.WorkingSetKind}");
        }

        lines.Add(string.Empty);
        lines.Add("Environment");
        if (result.Environment is { } environment)
        {
            lines.Add($"CpuName           : {environment.CpuName}");
            lines.Add($"PhysicalCores     : {environment.PhysicalCoreCount}");
            lines.Add($"LogicalProcessors : {environment.LogicalProcessorCount}");
            lines.Add($"NumaNodes         : {environment.NumaNodeCount}");
            lines.Add($"PowerPlan         : {environment.PowerPlan}");
            lines.Add($"OnAcPower         : {FormatNullable(environment.OnAcPower)}");
            lines.Add($"ProcessPriority   : {environment.ProcessPriority}");
        }

        lines.Add(string.Empty);
        lines.Add("Quality");
        if (result.Quality is { } quality)
        {
            lines.Add($"HighVariance              : {quality.HighVariance}");
            lines.Add($"BackgroundNoiseSuspected  : {quality.BackgroundNoiseSuspected}");
            lines.Add($"ShortDuration             : {quality.ShortDuration}");
            lines.Add($"ThermalSuspected          : {quality.ThermalSuspected}");
            lines.Add($"Flags                     : {(quality.Flags.Count == 0 ? "stable" : string.Join(", ", quality.Flags))}");
        }

        lines.Add(string.Empty);
        lines.Add("Metrics");
        if (result.Metrics is { } metrics)
        {
            AppendMetric(lines, "Read", metrics.Read);
            AppendMetric(lines, "Write", metrics.Write);
            AppendMetric(lines, "Copy", metrics.Copy);
            AppendMetric(lines, "Latency", metrics.Latency);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendMetric(List<string> lines, string name, MemoryBenchmarkMetricResult metric)
    {
        lines.Add(string.Empty);
        lines.Add(name);
        lines.Add($"Unit      : {metric.Unit}");
        lines.Add($"Median    : {metric.Aggregate.Median:F2}");
        lines.Add($"Min       : {metric.Aggregate.Min:F2}");
        lines.Add($"Max       : {metric.Aggregate.Max:F2}");
        lines.Add($"Mean      : {metric.Aggregate.Mean:F2}");
        lines.Add($"StdDev    : {metric.Aggregate.StdDev:F2}");
        lines.Add($"CV        : {metric.Aggregate.Cv:P2}");
        lines.Add($"Samples   : {string.Join(", ", metric.Samples.Select(sample => sample.ToString("F2", CultureInfo.InvariantCulture)))}");
    }

    private static string FormatNullable(double? value, string suffix)
    {
        return value is { } actual ? $"{actual.ToString("F2", CultureInfo.InvariantCulture)}{suffix}" : string.Empty;
    }

    private static string FormatNullable(bool? value)
    {
        return value?.ToString() ?? string.Empty;
    }
}
