using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using HwScope.Core.Benchmark.Storage;
using HwScope.Core.Hardware.Storage;
using Microsoft.Win32;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;

namespace HwScope.App;

public partial class StorageBenchmarkWindow : FluentWindow
{
    private readonly IStorageBenchmarkRunner _runner;
    private readonly StorageBenchmarkSessionStore _sessionStore = new();
    private readonly Dictionary<(string Workload, StorageBenchmarkOperation Operation), ResultCell> _cells;
    private CancellationTokenSource? _runCancellation;
    private StorageBenchmarkPlan? _activePlan;
    private StorageBenchmarkResult? _lastResult;
    private StorageBenchmarkTarget? _selectedTarget;
    private double? _temperatureBefore;
    private bool _healthCritical;
    private bool _isRunning;
    private bool _closeAfterRun;
    private bool _initialized;
    private IReadOnlyList<StorageBenchmarkOrphan> _orphans = [];

    public StorageBenchmarkWindow(IStorageBenchmarkRunner? runner = null)
    {
        _runner = runner ?? new StorageBenchmarkProcessRunner();
        InitializeComponent();
        _cells = new Dictionary<(string, StorageBenchmarkOperation), ResultCell>
        {
            [(StorageBenchmarkWorkloads.Sequential1MiBQ8T1, StorageBenchmarkOperation.Read)] = new(SeqQ8ReadValue, SeqQ8ReadDetail, SeqQ8ReadProgress),
            [(StorageBenchmarkWorkloads.Sequential1MiBQ8T1, StorageBenchmarkOperation.Write)] = new(SeqQ8WriteValue, SeqQ8WriteDetail, SeqQ8WriteProgress),
            [(StorageBenchmarkWorkloads.Sequential1MiBQ8T1, StorageBenchmarkOperation.Mix)] = new(SeqQ8MixValue, SeqQ8MixDetail, SeqQ8MixProgress),
            [(StorageBenchmarkWorkloads.Sequential1MiBQ1T1, StorageBenchmarkOperation.Read)] = new(SeqQ1ReadValue, SeqQ1ReadDetail, SeqQ1ReadProgress),
            [(StorageBenchmarkWorkloads.Sequential1MiBQ1T1, StorageBenchmarkOperation.Write)] = new(SeqQ1WriteValue, SeqQ1WriteDetail, SeqQ1WriteProgress),
            [(StorageBenchmarkWorkloads.Sequential1MiBQ1T1, StorageBenchmarkOperation.Mix)] = new(SeqQ1MixValue, SeqQ1MixDetail, SeqQ1MixProgress),
            [(StorageBenchmarkWorkloads.Random4KiBQ32T1, StorageBenchmarkOperation.Read)] = new(RndQ32ReadValue, RndQ32ReadDetail, RndQ32ReadProgress),
            [(StorageBenchmarkWorkloads.Random4KiBQ32T1, StorageBenchmarkOperation.Write)] = new(RndQ32WriteValue, RndQ32WriteDetail, RndQ32WriteProgress),
            [(StorageBenchmarkWorkloads.Random4KiBQ32T1, StorageBenchmarkOperation.Mix)] = new(RndQ32MixValue, RndQ32MixDetail, RndQ32MixProgress),
            [(StorageBenchmarkWorkloads.Random4KiBQ1T1, StorageBenchmarkOperation.Read)] = new(RndQ1ReadValue, RndQ1ReadDetail, RndQ1ReadProgress),
            [(StorageBenchmarkWorkloads.Random4KiBQ1T1, StorageBenchmarkOperation.Write)] = new(RndQ1WriteValue, RndQ1WriteDetail, RndQ1WriteProgress),
            [(StorageBenchmarkWorkloads.Random4KiBQ1T1, StorageBenchmarkOperation.Mix)] = new(RndQ1MixValue, RndQ1MixDetail, RndQ1MixProgress)
        };
        _initialized = true;
        Loaded += StorageBenchmarkWindow_Loaded;
        Closing += StorageBenchmarkWindow_Closing;
        UpdateMatrixAvailability();
    }

    private async void StorageBenchmarkWindow_Loaded(object sender, RoutedEventArgs e)
    {
        App.ThemeService.Attach(this);
        Loaded -= StorageBenchmarkWindow_Loaded;
        await LoadTargetsAsync();
        CheckForOrphans();
    }

    private void CheckForOrphans()
    {
        _orphans = _sessionStore.FindOrphans();
        CleanupOrphansButton.Visibility = _orphans.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_orphans.Count > 0)
        {
            TargetWarningText.Text = $"发现 {_orphans.Count} 个未完成 session 的测试文件。HwScope 只会清理通过 manifest 安全校验的文件。";
            TargetWarningText.Visibility = Visibility.Visible;
            StatusText.Text = "发现存储跑分残留文件，请先清理或确认后再运行。";
            StartCancelButton.IsEnabled = false;
        }
    }

    private void CleanupOrphansButton_Click(object sender, RoutedEventArgs e)
    {
        var results = _orphans.Select(_sessionStore.TryCleanup).ToList();
        CheckForOrphans();
        var failed = results.Count(result => !result.Deleted);
        StatusText.Text = failed == 0
            ? "已清理所有经过安全校验的存储跑分残留文件。"
            : $"有 {failed} 个残留文件未能安全清理；未通过校验的文件不会被删除。";
        UpdatePlanPreview();
    }

    private async Task LoadTargetsAsync()
    {
        SetPlanControlsEnabled(false);
        StartCancelButton.IsEnabled = false;
        StatusText.Text = "正在发现本地可写卷...";
        try
        {
            var discovered = await Task.Run(() => new StorageBenchmarkTargetDiscovery().Discover(App.StorageDetails.Devices)).ConfigureAwait(true);
            var targets = discovered.Select(EnrichFromStorageCache).ToList();
            TargetComboBox.ItemsSource = targets;
            if (targets.Count == 0)
            {
                TargetTitleText.Text = "没有可用的本地目标卷";
                TargetMetaText.Text = "网络路径、光盘、RAM disk 和未就绪卷不会进入目标列表。";
                StatusText.Text = "未发现可运行存储跑分的目标卷。";
                return;
            }

            TargetComboBox.SelectedItem = targets.FirstOrDefault(target => target.IsSystem) ?? targets[0];
            StatusText.Text = "准备就绪。运行前请确认目标卷和最大写入量。";
        }
        catch (Exception ex)
        {
            TargetTitleText.Text = "目标卷发现失败";
            TargetMetaText.Text = ex.Message;
            StatusText.Text = $"无法加载目标卷：{ex.Message}";
        }
        finally
        {
            SetPlanControlsEnabled(true);
            UpdatePlanPreview();
        }
    }

    private static StorageBenchmarkTarget EnrichFromStorageCache(StorageBenchmarkTarget target)
    {
        foreach (var device in App.StorageDetails.Devices)
        {
            var report = App.StorageDetails.TryGetCached(device.StableId);
            if (report is null || !report.Volumes.Any(volume =>
                    string.Equals(volume.DriveLetter, target.DriveLetter, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return target with
            {
                DeviceStableId = device.StableId,
                PhysicalDriveNumber = device.PhysicalDriveNumber,
                Model = report.Identity.Model.DisplayText,
                Bus = report.Interface.BusType.DisplayText,
                MediaType = report.Identity.MediaType.DisplayText,
                RequiredAlignmentBytes = checked((int)Math.Max(4096u, device.BytesPerSector ?? 0u))
            };
        }

        return target;
    }

    private async void TargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _isRunning)
        {
            return;
        }

        _selectedTarget = TargetComboBox.SelectedItem as StorageBenchmarkTarget;
        _temperatureBefore = null;
        _healthCritical = false;
        UpdateTargetDisplay();
        UpdatePlanPreview();
        if (_selectedTarget is { } target)
        {
            await LoadTargetHealthAsync(target);
        }
    }

    private async Task LoadTargetHealthAsync(StorageBenchmarkTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.DeviceStableId))
        {
            return;
        }

        try
        {
            var report = await App.StorageDetails.EnsureLoadedAsync(target.DeviceStableId).ConfigureAwait(true);
            if (_selectedTarget?.Id != target.Id)
            {
                return;
            }

            _temperatureBefore = report.Health.TemperatureCelsius.IsAvailable
                ? report.Health.TemperatureCelsius.Value
                : null;
            _healthCritical = report.Health.Status == StorageHealthStatus.Critical;
            TemperatureText.Text = FormatTemperature(_temperatureBefore);
            UpdatePlanPreview();
        }
        catch
        {
            if (_selectedTarget?.Id == target.Id)
            {
                TemperatureText.Text = "-- C";
            }
        }
    }

    private void PlanControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _isRunning)
        {
            return;
        }

        UpdateMatrixAvailability();
        UpdatePlanPreview();
    }

    private void UnitComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized)
        {
            return;
        }

        if (_lastResult is not null)
        {
            RenderResult(_lastResult);
        }
    }

    private async void StartCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            RequestCancel();
            return;
        }

        await StartBenchmarkAsync(StorageBenchmarkWorkloads.DisplayOrder);
    }

    private async void WorkloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRunning && sender is Button { Tag: string workload })
        {
            await StartBenchmarkAsync([workload]);
        }
    }

    private async Task StartBenchmarkAsync(IReadOnlyList<string> workloads)
    {
        if (_selectedTarget is null || _isRunning)
        {
            return;
        }

        StorageBenchmarkPlan plan;
        try
        {
            plan = StorageBenchmarkPlanner.CreatePlan(_selectedTarget, ReadOptions(), workloads);
            if (_healthCritical && plan.Workloads.Any(item => item.Operation is StorageBenchmarkOperation.Write or StorageBenchmarkOperation.Mix))
            {
                throw new InvalidOperationException("目标设备健康状态为严重。当前只允许切换到“仅读取”，不执行写入型跑分。");
            }
        }
        catch (Exception ex)
        {
            ShowInlineError(ex.Message);
            return;
        }

        _activePlan = plan;
        _lastResult = null;
        _runCancellation = new CancellationTokenSource();
        _isRunning = true;
        ClearResults();
        DiagnosticsButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        SetRunningState(true);
        StatusText.Text = "正在验证目标卷...";
        OverallProgress.Value = 0;
        var started = DateTimeOffset.Now;

        try
        {
            var progress = new Progress<StorageBenchmarkProgress>(UpdateProgress);
            var result = await _runner.RunAsync(plan, progress, _runCancellation.Token).ConfigureAwait(true);
            var temperatureAfter = await TryRefreshTemperatureAsync(plan.Target).ConfigureAwait(true);
            result = result with
            {
                TemperatureBeforeCelsius = _temperatureBefore,
                TemperatureAfterCelsius = temperatureAfter
            };
            _lastResult = result;
            RenderResult(result);
            OverallProgress.Value = 100;
            StatusText.Text = $"完成，用时 {(DateTimeOffset.Now - started).TotalSeconds:F1} 秒；实际读取 {StorageBenchmarkFormatting.FormatBytes(result.LogicalBytesRead)}，写入 {StorageBenchmarkFormatting.FormatBytes(result.LogicalBytesWritten)}；清理 {FormatCleanup(result.Cleanup)}。";
            DiagnosticsButton.IsEnabled = true;
            CopyButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消；worker 和父进程已完成残留文件检查。";
            DiagnosticsButton.IsEnabled = File.Exists(Path.Combine(Path.GetTempPath(), "HwScope-storage-benchmark.log"));
        }
        catch (Exception ex)
        {
            ShowInlineError(ex.Message);
            DiagnosticsButton.IsEnabled = File.Exists(Path.Combine(Path.GetTempPath(), "HwScope-storage-benchmark.log"));
        }
        finally
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            _activePlan = null;
            _isRunning = false;
            SetRunningState(false);
            if (_closeAfterRun)
            {
                _closeAfterRun = false;
                _ = Dispatcher.BeginInvoke(Close);
            }
        }
    }

    private async Task<double?> TryRefreshTemperatureAsync(StorageBenchmarkTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.DeviceStableId))
        {
            return null;
        }

        try
        {
            var report = await App.StorageDetails.RefreshAsync(target.DeviceStableId).ConfigureAwait(true);
            return report.Health.TemperatureCelsius.IsAvailable ? report.Health.TemperatureCelsius.Value : null;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateProgress(StorageBenchmarkProgress progress)
    {
        if (_activePlan is null || progress.SessionId != _activePlan.SessionId)
        {
            return;
        }

        switch (progress.Phase)
        {
            case "preflight":
                StatusText.Text = "正在验证目标卷、空间和 worker 协议...";
                OverallProgress.Value = 1;
                break;
            case "preparing":
                StatusText.Text = $"正在创建并初始化 {_activePlan.Options.FileSizeBytes / 1024 / 1024} MiB 测试文件...";
                OverallProgress.Value = 2 + (progress.Fraction ?? 0) * 8;
                break;
            case "cleanup":
                StatusText.Text = "测试完成，正在关闭 I/O 并删除临时文件...";
                OverallProgress.Value = 98;
                HideCellProgress();
                break;
            case "completed":
                OverallProgress.Value = 100;
                break;
            case "running":
                UpdateRunningProgress(progress);
                break;
        }
    }

    private void UpdateRunningProgress(StorageBenchmarkProgress progress)
    {
        if (progress.WorkloadId is null || progress.Operation is null || _activePlan is null)
        {
            return;
        }

        var key = (progress.WorkloadId, progress.Operation.Value);
        if (!_cells.TryGetValue(key, out var cell))
        {
            return;
        }

        HideCellProgress();
        var definition = StorageBenchmarkWorkloads.GetDefinition(progress.WorkloadId);
        var operation = FormatOperation(progress.Operation.Value);
        if (progress.Type == "sample_completed" && progress.ThroughputMBs is { } throughput)
        {
            cell.Progress.Visibility = Visibility.Visible;
            cell.Progress.Value = GetWorkloadFraction(progress) * 100;
            cell.Value.Text = FormatPrimary(throughput, progress.Iops ?? 0);
            cell.Detail.Text = $"第 {progress.SampleIndex}/{progress.SampleCount} 轮 · p95 {FormatLatency(progress.P95Microseconds ?? 0)}";
        }
        else if (progress.Type == "workload_progress")
        {
            cell.Progress.Visibility = Visibility.Visible;
            cell.Progress.Value = GetWorkloadFraction(progress) * 100;
            cell.Detail.Text = $"正在运行 {progress.SampleIndex}/{progress.SampleCount}";
        }
        else if (progress.Type == "workload_started")
        {
            cell.Progress.Visibility = Visibility.Visible;
            cell.Progress.Value = 0;
        }

        var planIndex = _activePlan.Workloads.ToList().FindIndex(item =>
            item.Id == progress.WorkloadId && item.Operation == progress.Operation);
        var overallFraction = (Math.Max(0, planIndex) + GetWorkloadFraction(progress)) / _activePlan.Workloads.Count;
        OverallProgress.Value = 10 + overallFraction * 87;
        StatusText.Text = progress.Type switch
        {
            "workload_completed" => $"已完成 {definition.DisplayName} {operation}",
            "workload_started" => $"正在测试 {definition.DisplayName} {operation}",
            _ => $"正在测试 {definition.DisplayName} {operation} · 第 {progress.SampleIndex ?? 1}/{progress.SampleCount ?? _activePlan.Options.Runs} 轮"
        };
    }

    private static double GetWorkloadFraction(StorageBenchmarkProgress progress)
    {
        if (progress.Fraction is { } fraction)
        {
            return fraction;
        }

        if (progress.Type == "workload_completed")
        {
            return 1;
        }

        if (progress.Type == "sample_completed" && progress.SampleIndex is { } index && progress.SampleCount is > 0)
        {
            return Math.Clamp((double)index / progress.SampleCount.Value, 0, 1);
        }

        return 0;
    }

    private void RenderResult(StorageBenchmarkResult result)
    {
        HideCellProgress();
        foreach (var row in result.Rows.Values)
        {
            RenderMetric(row.Id, StorageBenchmarkOperation.Read, row.Read);
            RenderMetric(row.Id, StorageBenchmarkOperation.Write, row.Write);
            RenderMetric(row.Id, StorageBenchmarkOperation.Mix, row.Mix);
        }

        TemperatureText.Text = result.TemperatureAfterCelsius is { } after
            ? $"{FormatTemperature(result.TemperatureBeforeCelsius)} -> {FormatTemperature(after)}"
            : FormatTemperature(result.TemperatureBeforeCelsius);
    }

    private void RenderMetric(string workload, StorageBenchmarkOperation operation, StorageBenchmarkMetricResult? metric)
    {
        if (metric is null || !_cells.TryGetValue((workload, operation), out var cell))
        {
            return;
        }

        cell.Value.Text = FormatPrimary(metric.Throughput.Median, metric.Iops.Median);
        cell.Detail.Text = IsIopsPrimary()
            ? $"{metric.Throughput.Median:F2} MB/s · p95 {FormatLatency(metric.Latency.P95Microseconds)}"
            : $"{FormatIops(metric.Iops.Median)} IOPS · p95 {FormatLatency(metric.Latency.P95Microseconds)}";
    }

    private void UpdatePlanPreview()
    {
        if (!_initialized || _isRunning)
        {
            return;
        }

        _selectedTarget = TargetComboBox.SelectedItem as StorageBenchmarkTarget;
        UpdateTargetDisplay();
        if (_selectedTarget is null)
        {
            StartCancelButton.IsEnabled = false;
            return;
        }

        try
        {
            var plan = StorageBenchmarkPlanner.CreatePlan(_selectedTarget, ReadOptions());
            PlanSummaryText.Text = $"文件 {StorageBenchmarkFormatting.FormatBytes(plan.Options.FileSizeBytes)} · 最多写入 {StorageBenchmarkFormatting.FormatBytes(plan.MaximumWriteBytes)} · {FormatCacheMode(plan.Options.CacheMode)}";
            TargetWarningText.Visibility = _selectedTarget.IsSystem || _healthCritical ? Visibility.Visible : Visibility.Collapsed;
            TargetWarningText.Text = _healthCritical
                ? "设备健康状态为严重：写入与 Mix 已被阻止，只允许读取测试。"
                : _selectedTarget.IsSystem
                    ? "系统盘前台负载会影响系统响应和成绩；请关闭大型文件传输与其他跑分。"
                    : string.Empty;
            StartCancelButton.IsEnabled = _orphans.Count == 0
                && (!_healthCritical || ReadOptions().Columns == StorageBenchmarkColumnMode.ReadOnly);
        }
        catch (Exception ex)
        {
            PlanSummaryText.Text = string.Empty;
            TargetWarningText.Text = ex.Message;
            TargetWarningText.Visibility = Visibility.Visible;
            StartCancelButton.IsEnabled = false;
        }
    }

    private void UpdateTargetDisplay()
    {
        if (_selectedTarget is null)
        {
            return;
        }

        var target = _selectedTarget;
        TargetTitleText.Text = string.IsNullOrWhiteSpace(target.Model) ? $"{target.DriveLetter} 本地卷" : target.Model;
        TargetMetaText.Text = string.Join(" · ", new[]
        {
            target.DriveLetter,
            target.Bus,
            target.FileSystem,
            $"可用 {StorageBenchmarkFormatting.FormatBytes(target.FreeSpaceBytes)} / {StorageBenchmarkFormatting.FormatBytes(target.TotalSizeBytes)}",
            target.IsSystem ? "系统盘" : null,
            target.IsRemovable ? "可移动" : null
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        TemperatureText.Text = FormatTemperature(_temperatureBefore);
    }

    private StorageBenchmarkOptions ReadOptions()
    {
        return new StorageBenchmarkOptions
        {
            Runs = int.Parse(ReadTag(RunsComboBox), CultureInfo.InvariantCulture),
            FileSizeBytes = long.Parse(ReadTag(SizeComboBox), CultureInfo.InvariantCulture),
            CacheMode = Enum.Parse<StorageBenchmarkCacheMode>(ReadTag(CacheModeComboBox)),
            Columns = Enum.Parse<StorageBenchmarkColumnMode>(ReadTag(ColumnModeComboBox)),
            MixReadPercent = 70
        };
    }

    private static string ReadTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? throw new InvalidOperationException("跑分参数尚未选择。");

    private void UpdateMatrixAvailability()
    {
        if (!_initialized)
        {
            return;
        }

        var columns = Enum.Parse<StorageBenchmarkColumnMode>(ReadTag(ColumnModeComboBox));
        var writeEnabled = columns != StorageBenchmarkColumnMode.ReadOnly;
        var mixEnabled = columns == StorageBenchmarkColumnMode.ReadWriteMix;
        MixHeaderText.Opacity = mixEnabled ? 1 : 0.45;
        foreach (var ((_, operation), cell) in _cells)
        {
            var enabled = operation == StorageBenchmarkOperation.Read
                || operation == StorageBenchmarkOperation.Write && writeEnabled
                || operation == StorageBenchmarkOperation.Mix && mixEnabled;
            if (_lastResult is null)
            {
                cell.Value.Text = "--";
                cell.Detail.Text = enabled ? "未运行" : "未启用";
            }
            cell.Value.Opacity = enabled ? 1 : 0.4;
            cell.Detail.Opacity = enabled ? 1 : 0.5;
        }
    }

    private void ClearResults()
    {
        var columns = ReadOptions().Columns;
        foreach (var ((_, operation), cell) in _cells)
        {
            var enabled = operation == StorageBenchmarkOperation.Read
                || operation == StorageBenchmarkOperation.Write && columns != StorageBenchmarkColumnMode.ReadOnly
                || operation == StorageBenchmarkOperation.Mix && columns == StorageBenchmarkColumnMode.ReadWriteMix;
            cell.Value.Text = "--";
            cell.Detail.Text = enabled ? "等待运行" : "未启用";
            cell.Progress.Value = 0;
            cell.Progress.Visibility = Visibility.Collapsed;
        }
    }

    private void SetRunningState(bool running)
    {
        StartCancelButton.Content = running ? "取消" : "全部开始";
        StartCancelButton.IsEnabled = true;
        SetPlanControlsEnabled(!running);
        SeqQ8Button.IsEnabled = !running;
        SeqQ1Button.IsEnabled = !running;
        RndQ32Button.IsEnabled = !running;
        RndQ1Button.IsEnabled = !running;
        if (!running)
        {
            UpdatePlanPreview();
        }
    }

    private void SetPlanControlsEnabled(bool enabled)
    {
        RunsComboBox.IsEnabled = enabled;
        SizeComboBox.IsEnabled = enabled;
        TargetComboBox.IsEnabled = enabled;
        CacheModeComboBox.IsEnabled = enabled;
        ColumnModeComboBox.IsEnabled = enabled;
    }

    private void RequestCancel()
    {
        StartCancelButton.Content = "正在取消";
        StartCancelButton.IsEnabled = false;
        StatusText.Text = "正在停止新 I/O、取消在途请求并清理测试文件...";
        _runCancellation?.Cancel();
    }

    private void StorageBenchmarkWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isRunning)
        {
            return;
        }

        e.Cancel = true;
        _closeAfterRun = true;
        RequestCancel();
    }

    private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        var text = _lastResult is not null
            ? StorageBenchmarkResultFormatter.Format(_lastResult)
            : ReadDiagnosticLog();
        new StorageBenchmarkDiagnosticsWindow(text) { Owner = this }.ShowDialog();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null)
        {
            return;
        }
        Clipboard.SetText(StorageBenchmarkResultFormatter.Format(_lastResult));
        StatusText.Text = "存储跑分报告已复制到剪贴板。";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存存储跑分报告",
            Filter = "文本报告 (*.txt)|*.txt|JSON 报告 (*.json)|*.json",
            FileName = $"HwScope-Storage-Benchmark-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var text = Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(_lastResult, new JsonSerializerOptions { WriteIndented = true })
            : StorageBenchmarkResultFormatter.Format(_lastResult);
        File.WriteAllText(dialog.FileName, text);
        StatusText.Text = $"报告已保存：{dialog.FileName}";
    }

    private void ShowInlineError(string message)
    {
        StatusText.Text = $"失败：{message}";
        TargetWarningText.Text = message;
        TargetWarningText.Visibility = Visibility.Visible;
    }

    private void HideCellProgress()
    {
        foreach (var cell in _cells.Values)
        {
            cell.Progress.Visibility = Visibility.Collapsed;
        }
    }

    private bool IsIopsPrimary() => ReadTag(UnitComboBox) == "IOPS";

    private string FormatPrimary(double throughput, double iops) => IsIopsPrimary()
        ? $"{FormatIops(iops)} IOPS"
        : $"{throughput:F2} MB/s";

    private static string FormatIops(double value) => value >= 1000 ? $"{value / 1000:F1}K" : $"{value:F0}";

    private static string FormatLatency(double microseconds) => microseconds >= 1000
        ? $"{microseconds / 1000:F2} ms"
        : $"{microseconds:F1} us";

    private static string FormatTemperature(double? value) => value is { } temperature ? $"{temperature:F0} C" : "-- C";

    private static string FormatOperation(StorageBenchmarkOperation operation) => operation switch
    {
        StorageBenchmarkOperation.Read => "Read",
        StorageBenchmarkOperation.Write => "Write",
        _ => "Mix"
    };

    private static string FormatCacheMode(StorageBenchmarkCacheMode mode) => mode == StorageBenchmarkCacheMode.Device
        ? "设备模式：禁用 Windows 文件缓存，设备缓存可能生效"
        : "系统缓存模式：成绩包含 Windows 文件缓存影响";

    private static string FormatCleanup(StorageBenchmarkCleanupResult cleanup) => cleanup.Deleted ? "完成" : $"未完成（{cleanup.Status}）";

    private static string ReadDiagnosticLog()
    {
        var path = Path.Combine(Path.GetTempPath(), "HwScope-storage-benchmark.log");
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "没有可用的存储跑分诊断日志。";
        }
        catch (IOException ex)
        {
            return $"无法读取诊断日志：{ex.Message}";
        }
    }

    private sealed record ResultCell(TextBlock Value, TextBlock Detail, ProgressBar Progress);
}
