using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HwScope.Core.Hardware.Memory;

public sealed class NativeSpdProcessProvider : ISpdProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly IReadOnlyList<string> _candidatePaths;
    private readonly TimeSpan _timeout;

    public NativeSpdProcessProvider()
        : this(GetDefaultCandidatePaths(), TimeSpan.FromSeconds(5))
    {
    }

    public NativeSpdProcessProvider(IReadOnlyList<string> candidatePaths, TimeSpan timeout)
    {
        _candidatePaths = candidatePaths;
        _timeout = timeout;
    }

    public SpdProviderResult TryCollect()
    {
        var workerPath = _candidatePaths.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(workerPath))
        {
            return SpdProviderResult.NotConfigured("未找到 native SPD worker；JEDEC/XMP/EXPO 时序保持待接入状态。");
        }

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                Arguments = "--json",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(_timeout))
            {
                process.Kill(entireProcessTree: true);
                return new SpdProviderResult(SpdProviderStatus.Timeout, [], ["native SPD worker 执行超时。"]);
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                return new SpdProviderResult(
                    SpdProviderStatus.Failed,
                    [],
                    [BuildExitDiagnostic(process.ExitCode, stderr)]);
            }

            return Parse(stdout, stderr);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SpdProviderResult(SpdProviderStatus.AccessDenied, [], [$"native SPD worker 无法访问：{ex.Message}"]);
        }
        catch (JsonException ex)
        {
            return new SpdProviderResult(SpdProviderStatus.ParseFailed, [], [$"native SPD worker JSON 解析失败：{ex.Message}"]);
        }
        catch (Exception ex)
        {
            return new SpdProviderResult(SpdProviderStatus.Failed, [], [$"native SPD worker 执行失败：{ex.Message}"]);
        }
    }

    private static SpdProviderResult Parse(string stdout, string stderr)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return new SpdProviderResult(SpdProviderStatus.ParseFailed, [], ["native SPD worker 没有输出 JSON。"]);
        }

        var payload = JsonSerializer.Deserialize<NativeSpdPayload>(stdout, JsonOptions)
            ?? throw new JsonException("empty payload");
        var status = MapStatus(payload.Status);
        var diagnostics = payload.Diagnostics?.Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic)).ToList() ?? [];
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            diagnostics.Add(stderr.Trim());
        }

        return new SpdProviderResult(
            status,
            payload.Modules?.Select(ToModule).ToList() ?? [],
            diagnostics);
    }

    private static SpdMemoryModule ToModule(NativeSpdModule module)
    {
        return new SpdMemoryModule(
            module.Locator ?? string.Empty,
            module.Type ?? string.Empty,
            module.ModuleType ?? string.Empty,
            module.CapacityBytes,
            module.Manufacturer ?? string.Empty,
            module.DramManufacturer ?? string.Empty,
            module.PartNumber ?? string.Empty,
            module.SerialNumber ?? string.Empty,
            module.ManufacturingWeek,
            module.ManufacturingYear,
            module.Revision ?? string.Empty,
            module.TimingProfiles?.Select(ToTimingProfile).ToList() ?? []);
    }

    private static SpdTimingProfile ToTimingProfile(NativeSpdTimingProfile profile)
    {
        return new SpdTimingProfile(
            profile.Name ?? string.Empty,
            profile.FrequencyMHz,
            profile.EffectiveRateMTps,
            profile.CasLatency ?? string.Empty,
            profile.Trcd ?? string.Empty,
            profile.Trp ?? string.Empty,
            profile.Tras ?? string.Empty,
            profile.Trc ?? string.Empty,
            profile.VoltageMv);
    }

    private static SpdProviderStatus MapStatus(string? status)
    {
        return status?.Trim() switch
        {
            "ok" => SpdProviderStatus.Ok,
            "workerMissing" => SpdProviderStatus.WorkerMissing,
            "accessDenied" => SpdProviderStatus.AccessDenied,
            "platformBlocked" => SpdProviderStatus.PlatformBlocked,
            "unsupportedMemoryType" => SpdProviderStatus.UnsupportedMemoryType,
            "checksumFailed" => SpdProviderStatus.ChecksumFailed,
            "parseFailed" => SpdProviderStatus.ParseFailed,
            "timeout" => SpdProviderStatus.Timeout,
            "failed" => SpdProviderStatus.Failed,
            _ => SpdProviderStatus.ParseFailed
        };
    }

    private static string BuildExitDiagnostic(int exitCode, string stderr)
    {
        var suffix = string.IsNullOrWhiteSpace(stderr) ? string.Empty : $"：{stderr.Trim()}";
        return $"native SPD worker 退出码 {exitCode}{suffix}";
    }

    private static IReadOnlyList<string> GetDefaultCandidatePaths()
    {
        var baseDirectory = AppContext.BaseDirectory;
        return
        [
            Path.Combine(baseDirectory, "spd.exe"),
            Path.Combine(baseDirectory, "native", "spd.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "HwScope.Native.Spd", "build", "Release", "spd.exe"))
        ];
    }

    private sealed record NativeSpdPayload(
        int SchemaVersion,
        string? Status,
        IReadOnlyList<NativeSpdModule>? Modules,
        IReadOnlyList<string>? Diagnostics);

    private sealed record NativeSpdModule(
        string? Locator,
        [property: JsonPropertyName("type")] string? Type,
        string? ModuleType,
        ulong CapacityBytes,
        string? Manufacturer,
        string? DramManufacturer,
        string? PartNumber,
        string? SerialNumber,
        int ManufacturingWeek,
        int ManufacturingYear,
        string? Revision,
        IReadOnlyList<NativeSpdTimingProfile>? TimingProfiles);

    private sealed record NativeSpdTimingProfile(
        string? Name,
        double FrequencyMHz,
        uint EffectiveRateMTps,
        string? CasLatency,
        string? Trcd,
        string? Trp,
        string? Tras,
        string? Trc,
        uint VoltageMv);
}
