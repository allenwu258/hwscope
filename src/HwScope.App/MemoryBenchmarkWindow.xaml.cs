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
            RenderRows(result);
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
        var value = progress.Metric == MemoryBenchmarkMetric.Latency
            ? $"{progress.Value:F1} ns"
            : FormatThroughputMiB(progress.Value);
        SetCell(progress.Row, progress.Metric, value);

        var rowName = FormatRowName(progress.Row);
        switch (progress.Metric)
        {
            case MemoryBenchmarkMetric.Read:
                StatusText.Text = $"{rowName} Read 完成，正在继续测试...";
                break;
            case MemoryBenchmarkMetric.Write:
                StatusText.Text = $"{rowName} Write 完成，正在继续测试...";
                break;
            case MemoryBenchmarkMetric.Copy:
                StatusText.Text = $"{rowName} Copy 完成，正在继续测试...";
                break;
            case MemoryBenchmarkMetric.Latency:
                StatusText.Text = $"{rowName} Latency 完成，正在整理结果...";
                break;
        }
    }

    private void ClearBenchmarkResults()
    {
        foreach (var row in MemoryBenchmarkRows.DisplayOrder)
        {
            foreach (MemoryBenchmarkMetric metric in Enum.GetValues<MemoryBenchmarkMetric>())
            {
                SetCell(row, metric, string.Empty);
            }
        }
    }

    private void RenderRows(MemoryBenchmarkResult result)
    {
        var rows = result.Rows ?? new Dictionary<string, MemoryBenchmarkRowResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var rowKey in MemoryBenchmarkRows.DisplayOrder)
        {
            if (!rows.TryGetValue(rowKey, out var row) || !row.Available || row.Metrics is not { } metrics)
            {
                SetRowUnavailable(rowKey);
                continue;
            }

            SetCell(rowKey, MemoryBenchmarkMetric.Read, FormatThroughputMiB(metrics.Read.Aggregate.Median));
            SetCell(rowKey, MemoryBenchmarkMetric.Write, FormatThroughputMiB(metrics.Write.Aggregate.Median));
            SetCell(rowKey, MemoryBenchmarkMetric.Copy, FormatThroughputMiB(metrics.Copy.Aggregate.Median));
            SetCell(rowKey, MemoryBenchmarkMetric.Latency, $"{metrics.Latency.Aggregate.Median:F1} ns");
        }
    }

    private void SetRowUnavailable(string row)
    {
        foreach (MemoryBenchmarkMetric metric in Enum.GetValues<MemoryBenchmarkMetric>())
        {
            SetCell(row, metric, "N/A");
        }
    }

    private void SetCell(string row, MemoryBenchmarkMetric metric, string value)
    {
        var target = (NormalizeRow(row), metric) switch
        {
            (MemoryBenchmarkRows.Memory, MemoryBenchmarkMetric.Read) => MemoryReadText,
            (MemoryBenchmarkRows.Memory, MemoryBenchmarkMetric.Write) => MemoryWriteText,
            (MemoryBenchmarkRows.Memory, MemoryBenchmarkMetric.Copy) => MemoryCopyText,
            (MemoryBenchmarkRows.Memory, MemoryBenchmarkMetric.Latency) => MemoryLatencyText,
            (MemoryBenchmarkRows.L1, MemoryBenchmarkMetric.Read) => L1ReadText,
            (MemoryBenchmarkRows.L1, MemoryBenchmarkMetric.Write) => L1WriteText,
            (MemoryBenchmarkRows.L1, MemoryBenchmarkMetric.Copy) => L1CopyText,
            (MemoryBenchmarkRows.L1, MemoryBenchmarkMetric.Latency) => L1LatencyText,
            (MemoryBenchmarkRows.L2, MemoryBenchmarkMetric.Read) => L2ReadText,
            (MemoryBenchmarkRows.L2, MemoryBenchmarkMetric.Write) => L2WriteText,
            (MemoryBenchmarkRows.L2, MemoryBenchmarkMetric.Copy) => L2CopyText,
            (MemoryBenchmarkRows.L2, MemoryBenchmarkMetric.Latency) => L2LatencyText,
            (MemoryBenchmarkRows.L3, MemoryBenchmarkMetric.Read) => L3ReadText,
            (MemoryBenchmarkRows.L3, MemoryBenchmarkMetric.Write) => L3WriteText,
            (MemoryBenchmarkRows.L3, MemoryBenchmarkMetric.Copy) => L3CopyText,
            (MemoryBenchmarkRows.L3, MemoryBenchmarkMetric.Latency) => L3LatencyText,
            _ => null
        };
        if (target is not null)
        {
            target.Text = value;
        }
    }

    private static string FormatThroughput(double value)
    {
        return $"{value.ToString("F0", CultureInfo.InvariantCulture)} MB/s";
    }

    private static string FormatThroughputMiB(double value)
    {
        return FormatThroughput(value * 1024.0 * 1024.0 / 1_000_000.0);
    }

    private static string NormalizeRow(string row)
    {
        return string.IsNullOrWhiteSpace(row) ? MemoryBenchmarkRows.Memory : row.Trim().ToLowerInvariant();
    }

    private static string FormatRowName(string row)
    {
        return NormalizeRow(row) switch
        {
            MemoryBenchmarkRows.L1 => "L1 Cache",
            MemoryBenchmarkRows.L2 => "L2 Cache",
            MemoryBenchmarkRows.L3 => "L3 Cache",
            _ => "Memory"
        };
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
            "Timer",
            $"Name              : {result.Timer?.Name ?? string.Empty}",
            $"FrequencyHz       : {result.Timer?.FrequencyHz.ToString(CultureInfo.InvariantCulture) ?? string.Empty}",
            string.Empty,
            "Options"
        };

        if (result.Options is { } options)
        {
            lines.Add($"SizeMiB           : {options.SizeMiB}");
            lines.Add($"Iterations        : {options.Iterations} (legacy min samples)");
            lines.Add($"WarmupRuns        : {options.WarmupRuns}");
            lines.Add($"MinSamples        : {options.MinSamples}");
            lines.Add($"MaxSamples        : {options.MaxSamples}");
            lines.Add($"TargetSampleMs    : {options.TargetSampleMs.ToString("F2", CultureInfo.InvariantCulture)}");
            lines.Add($"MaxCv             : {options.MaxCv.ToString("P2", CultureInfo.InvariantCulture)}");
            lines.Add($"LatencySteps      : {options.LatencySteps}");
            lines.Add($"Threads           : {options.Threads}");
            lines.Add($"ThreadMode        : {options.ThreadMode}");
            lines.Add($"NumaMode          : {options.NumaMode}");
            lines.Add($"Kernel            : {options.Kernel}");
            lines.Add($"StorePolicy       : {options.StorePolicy}");
            lines.Add($"UsePreferredCore  : {options.UsePreferredCore}");
            lines.Add($"WorkingSetKind    : {options.WorkingSetKind}");
        }

        lines.Add(string.Empty);
        lines.Add("Placement");
        if (result.Placement is { } placement)
        {
            lines.Add($"Mode              : {placement.Mode}");
            lines.Add($"Source            : {placement.Source}");
            lines.Add($"Confidence        : {placement.Confidence}");
            lines.Add($"Reason            : {placement.Reason}");
            lines.Add($"AffinityApplied   : {FormatNullable(placement.AffinityApplied)}");
            AppendProcessorPlacement(lines, "Requested", placement.Requested);
            AppendProcessorPlacement(lines, "Actual", placement.Actual);
            AppendProcessorPlacements(lines, "RequestedWorkers", placement.RequestedWorkers);
            AppendProcessorPlacements(lines, "ActualWorkers", placement.ActualWorkers);
            lines.Add($"Candidates        : {placement.Candidates.Count}");
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
        lines.Add("Rows");
        if (result.Rows is { } rows)
        {
            foreach (var rowKey in MemoryBenchmarkRows.DisplayOrder)
            {
                if (rows.TryGetValue(rowKey, out var row))
                {
                    AppendRow(lines, row);
                }
            }
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

    private static void AppendRow(List<string> lines, MemoryBenchmarkRowResult row)
    {
        lines.Add(string.Empty);
        lines.Add(row.DisplayName);
        lines.Add($"Available       : {row.Available}");
        if (!string.IsNullOrWhiteSpace(row.UnavailableReason))
        {
            lines.Add($"Unavailable     : {row.UnavailableReason}");
        }
        lines.Add($"WorkingSetBytes : {FormatNullable(row.WorkingSetBytes)}");
        lines.Add($"CacheLevel      : {row.CacheLevel?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}");
        lines.Add($"CacheSizeBytes  : {FormatNullable(row.CacheSizeBytes)}");
        lines.Add($"LineSizeBytes   : {row.LineSizeBytes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty}");
        lines.Add($"Source          : {row.Source ?? string.Empty}");
        if (row.Metrics is { } metrics)
        {
            AppendMetric(lines, $"{row.DisplayName} Read", metrics.Read);
            AppendMetric(lines, $"{row.DisplayName} Write", metrics.Write);
            AppendMetric(lines, $"{row.DisplayName} Copy", metrics.Copy);
            AppendMetric(lines, $"{row.DisplayName} Latency", metrics.Latency);
        }
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
        lines.Add($"Converged : {metric.Converged}");
        if (metric.InnerIterations.Count > 0)
        {
            lines.Add($"InnerLoop : {string.Join(", ", metric.InnerIterations.Select(sample => sample.ToString(CultureInfo.InvariantCulture)))}");
        }
        lines.Add($"Samples   : {string.Join(", ", metric.Samples.Select(sample => sample.ToString("F2", CultureInfo.InvariantCulture)))}");
        if (metric is MemoryBenchmarkCopyMetricResult copy && copy.TrafficAggregate is { } traffic)
        {
            lines.Add($"TrafficMedian : {traffic.Median:F2} {copy.Unit}");
            lines.Add($"TrafficSamples: {string.Join(", ", copy.TrafficSamples.Select(sample => sample.ToString("F2", CultureInfo.InvariantCulture)))}");
        }
    }

    private static void AppendProcessorPlacements(
        List<string> lines,
        string label,
        IReadOnlyList<MemoryBenchmarkProcessorPlacement> placements)
    {
        if (placements.Count == 0)
        {
            lines.Add($"{label,-17} : ");
            return;
        }

        for (var i = 0; i < placements.Count; i++)
        {
            AppendProcessorPlacement(lines, $"{label}[{i}]", placements[i]);
        }
    }

    private static void AppendProcessorPlacement(List<string> lines, string label, MemoryBenchmarkProcessorPlacement? placement)
    {
        if (placement is null)
        {
            lines.Add($"{label,-17} : ");
            return;
        }

        var metadata = new[]
        {
            placement.CoreIndex is { } core ? $"core {core}" : null,
            placement.PackageIndex is { } package ? $"package {package}" : null,
            placement.NumaNodeNumber is { } numa ? $"numa {numa}" : null,
            placement.SmtIndex is { } smt ? $"smt {smt}" : null,
            placement.EfficiencyClass is { } efficiency ? $"eff {efficiency}" : null,
            placement.HasSmt is { } hasSmt ? $"hasSmt {hasSmt}" : null
        }.Where(value => value is not null);

        lines.Add($"{label,-17} : group {placement.Group}, processor {placement.ProcessorNumber}"
            + (metadata.Any() ? $" ({string.Join(", ", metadata)})" : string.Empty));
    }

    private static string FormatNullable(double? value, string suffix)
    {
        return value is { } actual ? $"{actual.ToString("F2", CultureInfo.InvariantCulture)}{suffix}" : string.Empty;
    }

    private static string FormatNullable(long? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string FormatNullable(bool? value)
    {
        return value?.ToString() ?? string.Empty;
    }
}
