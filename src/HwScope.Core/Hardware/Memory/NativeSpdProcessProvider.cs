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
    private readonly IReadOnlyList<string> _arguments;

    public NativeSpdProcessProvider()
        : this(GetDefaultCandidatePaths(), TimeSpan.FromSeconds(5), GetDefaultArguments())
    {
    }

    public NativeSpdProcessProvider(IReadOnlyList<string> candidatePaths, TimeSpan timeout)
        : this(candidatePaths, timeout, ["--json"])
    {
    }

    public NativeSpdProcessProvider(IReadOnlyList<string> candidatePaths, TimeSpan timeout, IReadOnlyList<string> arguments)
    {
        _candidatePaths = candidatePaths;
        _timeout = timeout;
        _arguments = arguments.Count > 0 ? arguments : ["--json"];
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
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in _arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

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
            ToOrganization(module.Organization),
            ToVoltages(module.Voltages),
            ToRaw(module.Raw),
            module.TimingProfiles?.Select(ToTimingProfile).ToList() ?? [],
            module.Features?.Select(feature => new SpdModuleFeature(feature.Name ?? string.Empty, JsonValueToString(feature.Value))).ToList() ?? [],
            module.Diagnostics?.Where(diagnostic => !string.IsNullOrWhiteSpace(diagnostic)).Select(diagnostic => diagnostic.Trim()).ToList() ?? []);
    }

    private static SpdModuleOrganization ToOrganization(NativeSpdOrganization? organization)
    {
        return organization is null
            ? new SpdModuleOrganization(0, 0, 0, 0, 0, 0, 0)
            : new SpdModuleOrganization(
                organization.RankCount,
                organization.BankGroupCount,
                organization.BanksPerGroup,
                organization.DeviceWidthBits,
                organization.BusWidthBits,
                organization.DataWidthBits,
                organization.TotalWidthBits);
    }

    private static SpdModuleVoltages ToVoltages(NativeSpdVoltages? voltages)
    {
        return voltages is null
            ? new SpdModuleVoltages(0, 0, 0)
            : new SpdModuleVoltages(voltages.VddMv, voltages.VddqMv, voltages.VppMv);
    }

    private static SpdRawInfo ToRaw(NativeSpdRaw? raw)
    {
        return raw is null
            ? new SpdRawInfo(0, null, null, string.Empty)
            : new SpdRawInfo(raw.ByteCount, raw.ChecksumOk, raw.CrcOk, raw.Sha256 ?? string.Empty);
    }

    private static SpdTimingProfile ToTimingProfile(NativeSpdTimingProfile profile)
    {
        return new SpdTimingProfile(
            profile.Name ?? string.Empty,
            profile.Kind ?? string.Empty,
            profile.FrequencyMHz,
            profile.EffectiveRateMTps,
            JsonValueToString(profile.CasLatency),
            JsonValueToString(profile.Trcd),
            JsonValueToString(profile.Trp),
            JsonValueToString(profile.Tras),
            JsonValueToString(profile.Trc),
            profile.VoltageMv);
    }

    private static string JsonValueToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static SpdProviderStatus MapStatus(string? status)
    {
        return status?.Trim() switch
        {
            "ok" => SpdProviderStatus.Ok,
            "workerMissing" => SpdProviderStatus.WorkerMissing,
            "accessDenied" => SpdProviderStatus.AccessDenied,
            "platformBlocked" => SpdProviderStatus.PlatformBlocked,
            "notImplemented" => SpdProviderStatus.NotImplemented,
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

    private static IReadOnlyList<string> GetDefaultArguments()
    {
        var fixture = Environment.GetEnvironmentVariable("HWSCOPE_SPD_FIXTURE");
        return string.IsNullOrWhiteSpace(fixture)
            ? ["--json"]
            : ["--json", "--backend", "fixture", "--fixture", fixture];
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
        NativeSpdOrganization? Organization,
        NativeSpdVoltages? Voltages,
        NativeSpdRaw? Raw,
        IReadOnlyList<NativeSpdTimingProfile>? TimingProfiles,
        IReadOnlyList<NativeSpdFeature>? Features,
        IReadOnlyList<string>? Diagnostics);

    private sealed record NativeSpdOrganization(
        int RankCount,
        int BankGroupCount,
        int BanksPerGroup,
        int DeviceWidthBits,
        int BusWidthBits,
        int DataWidthBits,
        int TotalWidthBits);

    private sealed record NativeSpdVoltages(
        uint VddMv,
        uint VddqMv,
        uint VppMv);

    private sealed record NativeSpdRaw(
        int ByteCount,
        bool? ChecksumOk,
        bool? CrcOk,
        string? Sha256);

    private sealed record NativeSpdFeature(
        string? Name,
        JsonElement Value);

    private sealed record NativeSpdTimingProfile(
        string? Name,
        string? Kind,
        double FrequencyMHz,
        uint EffectiveRateMTps,
        JsonElement CasLatency,
        JsonElement Trcd,
        JsonElement Trp,
        JsonElement Tras,
        JsonElement Trc,
        uint VoltageMv);
}
