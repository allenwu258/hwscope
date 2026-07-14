using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HwScope.Core.Benchmark.Storage;

public sealed class StorageBenchmarkProcessRunner : IStorageBenchmarkRunner
{
    internal const int ExpectedProtocolVersion = 1;
    private static readonly TimeSpan CooperativeCancelTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly string? _executablePath;
    private readonly string _diagnosticLogPath;
    private readonly StorageBenchmarkSessionStore _sessionStore;

    public StorageBenchmarkProcessRunner(
        string? executablePath = null,
        string? diagnosticLogPath = null,
        StorageBenchmarkSessionStore? sessionStore = null)
    {
        _executablePath = executablePath;
        _diagnosticLogPath = diagnosticLogPath
            ?? Path.Combine(Path.GetTempPath(), "HwScope-storage-benchmark.log");
        _sessionStore = sessionStore ?? new StorageBenchmarkSessionStore();
    }

    public async Task<StorageBenchmarkResult> RunAsync(
        StorageBenchmarkPlan plan,
        IProgress<StorageBenchmarkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var executable = ResolveExecutablePath()
            ?? throw new FileNotFoundException("未找到 storagebench.exe。请先构建 src/HwScope.Native.StorageBench，或确认产物已复制到应用输出目录的 native 子目录。");

        using var volumeLease = AcquireVolumeLock(plan.Target.Id);

        var freeBefore = StorageBenchmarkPreflight.Prepare(plan);
        _sessionStore.Create(plan);
        var arguments = BuildArguments(plan);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = StartProcess(startInfo, plan);
        var outputTask = ReadOutputAsync(process.StandardOutput, plan, progress);
        var errorTask = process.StandardError.ReadToEndAsync();
        var waitTask = process.WaitForExitAsync(CancellationToken.None);
        using var timeoutCts = new CancellationTokenSource(plan.Options.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token);

        try
        {
            if (await Task.WhenAny(waitTask, cancellationTask).ConfigureAwait(false) != waitTask)
            {
                await RequestCancelAsync(process).ConfigureAwait(false);
                if (await Task.WhenAny(waitTask, Task.Delay(CooperativeCancelTimeout)).ConfigureAwait(false) != waitTask)
                {
                    await KillProcessTreeAsync(process).ConfigureAwait(false);
                }
                else
                {
                    await waitTask.ConfigureAwait(false);
                }

                var canceledOutput = await CompleteOutputAsync(outputTask).ConfigureAwait(false);
                var canceledError = await CompleteReadAsync(errorTask).ConfigureAwait(false);
                var cleanup = TryDeleteTestFile(plan);
                var reason = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested ? "Timeout" : "Canceled";
                var logPath = WriteDiagnostics(reason, executable, arguments, process.HasExited ? process.ExitCode : null, canceledOutput, canceledError, cleanup);
                if (reason == "Timeout")
                {
                    throw new TimeoutException($"存储跑分超过 {plan.Options.Timeout.TotalSeconds:F0} 秒，已终止 worker。清理状态：{cleanup.Status}。诊断日志：{logPath}");
                }

                throw new OperationCanceledException($"存储跑分已取消。清理状态：{cleanup.Status}。诊断日志：{logPath}", cancellationToken);
            }

            await waitTask.ConfigureAwait(false);
            var capture = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var cleanup = TryDeleteTestFile(plan);
                var logPath = WriteDiagnostics("NonZeroExit", executable, arguments, process.ExitCode, capture.Text, error, cleanup);
                throw new InvalidOperationException($"存储跑分失败，worker 退出码 {process.ExitCode}：{FormatBriefError(error)}。清理状态：{cleanup.Status}。诊断日志：{logPath}");
            }

            if (capture.Result is null || !capture.Completed)
            {
                throw new FormatException("存储跑分协议缺少完整 result/completed 事件。");
            }

            ValidateWorkerResult(capture.Result, plan);
            var freeAfter = TryGetFreeSpace(plan.Target.RootPath);
            return Enrich(capture.Result, plan, executable, freeBefore, freeAfter);
        }
        catch
        {
            if (!process.HasExited)
            {
                await KillProcessTreeAsync(process).ConfigureAwait(false);
            }
            TryDeleteTestFile(plan);
            throw;
        }
        finally
        {
            _sessionStore.Complete(plan);
        }
    }

    private Process StartProcess(ProcessStartInfo startInfo, StorageBenchmarkPlan plan)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动存储跑分 worker。");
        }
        catch
        {
            _sessionStore.Complete(plan);
            throw;
        }
    }

    private string? ResolveExecutablePath()
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(_executablePath))
        {
            candidates.Add(_executablePath);
        }

        var baseDirectory = AppContext.BaseDirectory;
        candidates.Add(Path.Combine(baseDirectory, "storagebench.exe"));
        candidates.Add(Path.Combine(baseDirectory, "native", "storagebench.exe"));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "HwScope.Native.StorageBench", "build", "Release", "storagebench.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }

    internal static IReadOnlyList<string> BuildArguments(StorageBenchmarkPlan plan)
    {
        var arguments = new List<string>
        {
            "--path", plan.TestFilePath,
            "--session-id", plan.SessionId,
            "--file-size-bytes", plan.Options.FileSizeBytes.ToString(CultureInfo.InvariantCulture),
            "--write-budget-bytes", plan.MaximumWriteBytes.ToString(CultureInfo.InvariantCulture),
            "--runs", plan.Options.Runs.ToString(CultureInfo.InvariantCulture),
            "--warmup-passes", plan.Options.WarmupPasses.ToString(CultureInfo.InvariantCulture),
            "--mix-read-percent", plan.Options.MixReadPercent.ToString(CultureInfo.InvariantCulture),
            "--alignment-bytes", plan.Target.RequiredAlignmentBytes.ToString(CultureInfo.InvariantCulture),
            "--expected-protocol-version", ExpectedProtocolVersion.ToString(CultureInfo.InvariantCulture),
            "--cache-mode", plan.Options.CacheMode == StorageBenchmarkCacheMode.Device ? "device" : "buffered",
            "--columns", plan.Options.Columns switch
            {
                StorageBenchmarkColumnMode.ReadOnly => "read-only",
                StorageBenchmarkColumnMode.ReadWrite => "read-write",
                StorageBenchmarkColumnMode.ReadWriteMix => "read-write-mix",
                _ => throw new ArgumentOutOfRangeException(nameof(plan))
            }
        };
        foreach (var workload in plan.Workloads.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            arguments.Add("--workload");
            arguments.Add(workload);
        }
        arguments.Add("--progress-json");
        return arguments;
    }

    private static VolumeLockLease AcquireVolumeLock(string targetId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(targetId)))[..24];
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HwScope",
            "StorageBench",
            "Locks");
        Directory.CreateDirectory(directory);
        try
        {
            var stream = new FileStream(
                Path.Combine(directory, $"{hash}.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            return new VolumeLockLease(stream);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("选中的目标卷已经有一个 HwScope 存储跑分正在运行。", ex);
        }
    }

    private static async Task RequestCancelAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("cancel").ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
    }

    private static async Task KillProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            return;
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<OutputCapture> ReadOutputAsync(
        StreamReader reader,
        StorageBenchmarkPlan plan,
        IProgress<StorageBenchmarkProgress>? progress)
    {
        var lines = new List<string>();
        StorageWorkerResult? result = null;
        var completed = false;
        long lastSequence = 0;
        while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            lines.Add(line);
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString() ?? throw new FormatException("协议事件缺少 type。");
            if (type == "result")
            {
                result = JsonSerializer.Deserialize<StorageWorkerResult>(line, JsonOptions)
                    ?? throw new FormatException("无法解析存储跑分 result。");
                continue;
            }

            var protocol = root.GetProperty("protocol_version").GetInt32();
            var session = root.GetProperty("session_id").GetString();
            var sequence = root.GetProperty("sequence").GetInt64();
            if (protocol != ExpectedProtocolVersion || !string.Equals(session, plan.SessionId, StringComparison.Ordinal)
                || sequence <= lastSequence)
            {
                throw new FormatException("存储跑分 progress protocol/version/session/sequence 无效。");
            }
            lastSequence = sequence;
            if (type == "completed")
            {
                completed = true;
            }

            var update = ParseProgress(root, type, session!, sequence);
            progress?.Report(update);
        }

        return new OutputCapture(string.Join(Environment.NewLine, lines), result, completed);
    }

    internal static StorageBenchmarkProgress ParseProgressLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString() ?? string.Empty;
        return ParseProgress(
            root,
            type,
            root.GetProperty("session_id").GetString() ?? string.Empty,
            root.GetProperty("sequence").GetInt64());
    }

    private static StorageBenchmarkProgress ParseProgress(JsonElement root, string type, string session, long sequence)
    {
        return new StorageBenchmarkProgress(
            Type: type,
            SessionId: session,
            Sequence: sequence,
            Phase: GetString(root, "phase") ?? type,
            WorkloadId: GetString(root, "workload_id"),
            Operation: ParseOperation(GetString(root, "operation")),
            SampleIndex: GetInt(root, "sample_index"),
            SampleCount: GetInt(root, "sample_count"),
            CompletedBytes: GetLong(root, "completed_bytes"),
            PlannedBytes: GetLong(root, "planned_bytes"),
            ThroughputMBs: GetDouble(root, "throughput_mb_s"),
            Iops: GetDouble(root, "iops"),
            P95Microseconds: GetDouble(root, "p95_microseconds"));
    }

    private static StorageBenchmarkOperation? ParseOperation(string? value)
    {
        return value switch
        {
            "read" => StorageBenchmarkOperation.Read,
            "write" => StorageBenchmarkOperation.Write,
            "mix" => StorageBenchmarkOperation.Mix,
            _ => null
        };
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetString() : null;

    private static int? GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetInt32() : null;

    private static long? GetLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetInt64() : null;

    private static double? GetDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetDouble() : null;

    private static void ValidateWorkerResult(StorageWorkerResult result, StorageBenchmarkPlan plan)
    {
        if (!string.Equals(result.Type, "result", StringComparison.Ordinal)
            || !string.Equals(result.SessionId, plan.SessionId, StringComparison.Ordinal)
            || result.ProtocolVersion != ExpectedProtocolVersion
            || result.FileSizeBytes != plan.Options.FileSizeBytes)
        {
            throw new FormatException("存储跑分最终结果与计划不匹配。");
        }

        var expectedRows = plan.Workloads.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expectedRows.SetEquals(result.Rows.Keys))
        {
            throw new FormatException("存储跑分结果 workload 集合与计划不匹配。");
        }

        if (result.LogicalBytesWritten > plan.MaximumWriteBytes)
        {
            throw new FormatException("worker 报告的实际写入量超过本次 workload 计划上限。");
        }
    }

    private static StorageBenchmarkResult Enrich(
        StorageWorkerResult worker,
        StorageBenchmarkPlan plan,
        string executable,
        long freeBefore,
        long? freeAfter)
    {
        var metrics = worker.Rows.Values.SelectMany(row => new[] { row.Read, row.Write, row.Mix }).Where(metric => metric is not null).Cast<StorageBenchmarkMetricResult>().ToList();
        var highVariance = metrics.Any(metric => metric.Throughput.Cv > 0.08);
        var shortSample = metrics.SelectMany(metric => metric.Samples).Any(sample => sample.ElapsedMs < 500);
        var flags = new List<string> { "flushOutsideTimedRegion" };
        flags.Add(plan.Options.CacheMode == StorageBenchmarkCacheMode.Buffered ? "osCacheEnabled" : "deviceCacheMayBeActive");
        if (plan.Target.IsSystem) flags.Add("systemVolume");
        if (plan.Target.IsRemovable) flags.Add("removableTarget");
        if (highVariance) flags.Add("highVariance");
        if (shortSample) flags.Add("sampleTooShort");
        if (!worker.Cleanup.Deleted) flags.Add("cleanupIncomplete");

        return new StorageBenchmarkResult(
            worker.SessionId,
            worker.WorkerVersion,
            worker.ProtocolVersion,
            worker.ElapsedMs,
            worker.FileSizeBytes,
            worker.LogicalBytesRead,
            worker.LogicalBytesWritten,
            worker.CacheMode,
            worker.Rows,
            worker.Cleanup,
            DateTimeOffset.Now,
            plan,
            new StorageBenchmarkQuality(highVariance, shortSample, flags),
            executable,
            freeBefore,
            freeAfter);
    }

    private static long? TryGetFreeSpace(string rootPath)
    {
        try
        {
            return new DriveInfo(rootPath).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static StorageBenchmarkCleanupResult TryDeleteTestFile(StorageBenchmarkPlan plan)
    {
        try
        {
            var expectedName = $"hwscope-storagebench-{plan.SessionId}.tmp";
            var fullPath = Path.GetFullPath(plan.TestFilePath);
            if (!string.Equals(Path.GetFileName(fullPath), expectedName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(plan.Target.TestDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                return new StorageBenchmarkCleanupResult(false, false, "pathRejected");
            }

            if (!File.Exists(fullPath))
            {
                return new StorageBenchmarkCleanupResult(true, true, "notFound");
            }

            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                return new StorageBenchmarkCleanupResult(true, false, "reparsePointRejected");
            }

            File.Delete(fullPath);
            return new StorageBenchmarkCleanupResult(true, true, "deletedByParent");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StorageBenchmarkCleanupResult(true, false, ex.GetType().Name);
        }
    }

    private async Task<string> CompleteOutputAsync(Task<OutputCapture> outputTask)
    {
        try
        {
            return (await outputTask.ConfigureAwait(false)).Text;
        }
        catch (Exception ex)
        {
            return $"<无法读取/解析 worker stdout：{ex.Message}>";
        }
    }

    private static async Task<string> CompleteReadAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return $"<无法读取 worker stderr：{ex.Message}>";
        }
    }

    private string WriteDiagnostics(
        string reason,
        string executable,
        IReadOnlyList<string> arguments,
        int? exitCode,
        string output,
        string error,
        StorageBenchmarkCleanupResult cleanup)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_diagnosticLogPath) ?? Path.GetTempPath());
            var sanitizedArguments = arguments.Select(argument => argument.Contains("hwscope-storagebench-", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(argument)
                : argument);
            var text = string.Join(Environment.NewLine,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {reason}",
                $"Executable: {executable}",
                $"Arguments : {string.Join(' ', sanitizedArguments)}",
                $"ExitCode  : {(exitCode?.ToString(CultureInfo.InvariantCulture) ?? "<none>")}",
                $"Cleanup   : {cleanup.Status} / deleted={cleanup.Deleted}",
                "Stdout:", string.IsNullOrWhiteSpace(output) ? "<empty>" : output.TrimEnd(),
                "Stderr:", string.IsNullOrWhiteSpace(error) ? "<empty>" : error.TrimEnd(),
                new string('-', 80), string.Empty);
            File.AppendAllText(_diagnosticLogPath, text);
            return _diagnosticLogPath;
        }
        catch (Exception ex)
        {
            return $"{_diagnosticLogPath}（写入失败：{ex.Message}）";
        }
    }

    private static string FormatBriefError(string error) =>
        string.IsNullOrWhiteSpace(error) ? "无 stderr 输出" : error.Trim();

    private sealed record OutputCapture(string Text, StorageWorkerResult? Result, bool Completed);

    private sealed class VolumeLockLease(FileStream stream) : IDisposable
    {
        public void Dispose() => stream.Dispose();
    }
}
