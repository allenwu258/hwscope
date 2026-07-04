using System.Globalization;
using System.Text.RegularExpressions;
using HwScope.Core.Hardware.Inventory;

namespace HwScope.Core.Hardware.Cpu;

public sealed class CpuDetailCollector
{
    public CpuDetailReport Collect()
    {
        return CreateReport(new HardwareInventoryCollector().Collect());
    }

    public CpuDetailReport CreateReport(HardwareInventorySnapshot snapshot)
    {
        var processors = snapshot.Processors;
        var cpu = processors.FirstOrDefault();
        var topologyAnalysis = snapshot.CpuTopology;

        var processorName = CleanName(cpu?.Name);
        var knownInfo = CpuKnownProcessorCatalog.Match(processorName);
        var notes = new List<CpuDataNote>();

        if (knownInfo is not null)
        {
            notes.Add(new CpuDataNote("带 * 的映射值来自本地处理器资料库，用于补充 WMI 未提供的代号、工艺、TDP、缓存和指令集。", CpuDataSource.Mapping));
        }

        notes.Add(new CpuDataNote("WMI 是当前主要数据源；CPUID、Windows 拓扑 API 和传感器字段会在后续阶段继续补齐。", CpuDataSource.Wmi));
        if (topologyAnalysis is not null)
        {
            notes.AddRange(topologyAnalysis.Notes);
        }

        var coreCount = processors.Sum(processor => processor.NumberOfCores);
        var logicalCount = processors.Sum(processor => processor.NumberOfLogicalProcessors);
        var currentClock = Positive(snapshot.ProcessorFrequencyMHz) ?? Positive(cpu?.CurrentClockSpeed ?? 0);
        var maxClock = Positive(cpu?.MaxClockSpeed ?? 0);
        var busClock = EstimateBusClock(currentClock, maxClock);
        var multiplier = currentClock is > 0 && busClock is > 0 ? currentClock / busClock : null;
        var wmiPackage = FirstUseful(cpu?.SocketDesignation);
        var packageValue = FirstUseful(wmiPackage, knownInfo?.Package);
        var packageSource = IsUseful(wmiPackage) ? CpuDataSource.Wmi : knownInfo is null ? CpuDataSource.Unknown : CpuDataSource.Mapping;
        var packageEstimated = !IsUseful(wmiPackage) && knownInfo is not null;

        return new CpuDetailReport(
            Identity: new CpuIdentity(
                DisplayName: CpuField.Text(knownInfo?.DisplayName ?? ParseDisplayName(processorName), knownInfo is null ? CpuDataSource.Wmi : CpuDataSource.Mapping, isEstimated: knownInfo is not null),
                SpecificationName: CpuField.Text(processorName, CpuDataSource.Wmi),
                Vendor: CpuField.Text(NormalizeVendor(cpu?.Manufacturer ?? string.Empty), CpuDataSource.Wmi),
                CodeName: CpuField.Text(knownInfo?.CodeName, knownInfo is null ? CpuDataSource.Unknown : CpuDataSource.Mapping, CpuField.PendingCpuidText, isEstimated: knownInfo is not null)),
            Specification: new CpuSpecification(
                Package: CpuField.Text(packageValue, packageSource, isEstimated: packageEstimated),
                Technology: CpuField.Text(knownInfo?.Technology, knownInfo is null ? CpuDataSource.Unknown : CpuDataSource.Mapping, CpuField.PendingCpuidText, isEstimated: knownInfo is not null),
                Tdp: CpuField.Text(knownInfo?.Tdp, knownInfo is null ? CpuDataSource.Unknown : CpuDataSource.Mapping, CpuField.PendingCpuidText, isEstimated: knownInfo is not null),
                CoreVoltage: CpuField.Placeholder<string>("待接入传感器"),
                Family: CpuField.Text(FormatUInt(cpu?.Family ?? 0), CpuDataSource.Wmi),
                Model: CpuField.Placeholder<string>(),
                Stepping: CpuField.Text(cpu?.Stepping ?? string.Empty, CpuDataSource.Wmi),
                ExtendedFamily: CpuField.Placeholder<string>(),
                ExtendedModel: CpuField.Placeholder<string>(),
                Revision: CpuField.Text(knownInfo?.Revision ?? FormatRevision(cpu?.Revision ?? 0), knownInfo is null ? CpuDataSource.Wmi : CpuDataSource.Mapping, isEstimated: knownInfo is not null)),
            Topology: topologyAnalysis?.Topology ?? new CpuTopology(
                PackageCount: CpuField.Number(processors.Count, CpuDataSource.Wmi),
                CoreCount: CpuField.Number((int)coreCount, CpuDataSource.Wmi),
                LogicalProcessorCount: CpuField.Number((int)logicalCount, CpuDataSource.Wmi),
                SmtEnabled: CpuField.Boolean(coreCount > 0 && logicalCount > coreCount, CpuDataSource.Computed, isEstimated: true),
                CpuGroupCount: CpuField.Placeholder<int>("待接入 Windows 拓扑 API"),
                NumaNodeCount: CpuField.Placeholder<int>("待接入 Windows 拓扑 API")),
            Clocks: new CpuClockInfo(
                CurrentMHz: CpuField.MHz(currentClock, CpuDataSource.Wmi),
                BaseMHz: CpuField.MHz(maxClock, CpuDataSource.Wmi),
                MaxMHz: CpuField.MHz(maxClock, CpuDataSource.Wmi),
                BusMHz: CpuField.MHz(busClock, CpuDataSource.Computed, isEstimated: true, note: "按常见 100 MHz 基准时钟估算。"),
                Multiplier: CpuField.Ratio(multiplier, CpuDataSource.Computed, isEstimated: true)),
            Caches: topologyAnalysis?.Caches ?? knownInfo?.Caches ?? CreatePlaceholderCaches(),
            CoreMappings: topologyAnalysis?.CoreMappings ?? [],
            TopologyInspect: topologyAnalysis?.InspectReport,
            Features: knownInfo?.Features ?? CreatePlaceholderFeatures(),
            Platform: new CpuPlatformContext(
                Motherboard: CpuField.Text(JoinUseful(snapshot.BaseBoard?.Manufacturer, snapshot.BaseBoard?.Product), CpuDataSource.Wmi),
                BiosVersion: CpuField.Text(FirstUseful(snapshot.Bios?.SmbiosBiosVersion, snapshot.Bios?.Version), CpuDataSource.Wmi),
                Chipset: CpuField.Placeholder<string>(),
                IntegratedVideo: CpuField.Text(CollectIntegratedVideo(snapshot.VideoControllers), CpuDataSource.Wmi, isEstimated: true),
                MemoryType: CpuField.Text(CollectMemoryType(snapshot.MemoryModules), CpuDataSource.Wmi),
                MemoryClock: CpuField.Text(CollectMemoryClock(snapshot.MemoryModules), CpuDataSource.Wmi),
                DramFsbRatio: CpuField.Placeholder<string>()),
            Notes: notes,
            GeneratedAt: snapshot.GeneratedAt);
    }

    private static IReadOnlyList<CpuCacheInfo> CreatePlaceholderCaches()
    {
        return
        [
            PlaceholderCache(CpuCacheLevel.L1Data, "L1 Data"),
            PlaceholderCache(CpuCacheLevel.L1Instruction, "L1 Instruction"),
            PlaceholderCache(CpuCacheLevel.L2, "L2"),
            PlaceholderCache(CpuCacheLevel.L3, "L3")
        ];
    }

    private static CpuCacheInfo PlaceholderCache(CpuCacheLevel level, string name)
    {
        return new CpuCacheInfo(level, name, null, null, null, null, null, null, [], CpuDataSource.Placeholder, Note: "待接入 native CPUID 或 Windows 拓扑 API。");
    }

    private static IReadOnlyList<CpuFeature> CreatePlaceholderFeatures()
    {
        return
        [
            new CpuFeature(CpuField.PendingCpuidText, CpuFeatureGroup.Other, IsSupported: false, CpuDataSource.Placeholder)
        ];
    }

    private static double? Positive(uint value)
    {
        return value > 0 ? value : null;
    }

    private static double? EstimateBusClock(double? currentClock, double? maxClock)
    {
        var clock = currentClock is > 0 ? currentClock.Value : maxClock;
        if (clock is null or <= 0)
        {
            return null;
        }

        return 100.0;
    }

    private static string ParseDisplayName(string processorName)
    {
        if (string.IsNullOrWhiteSpace(processorName))
        {
            return string.Empty;
        }

        var normalized = Regex.Replace(processorName, @"\s+w/\s+.*$", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s+with\s+.*$", string.Empty, RegexOptions.IgnoreCase);
        return CleanName(normalized);
    }

    private static string NormalizeVendor(string manufacturer)
    {
        if (manufacturer.Contains("AMD", StringComparison.OrdinalIgnoreCase)
            || manufacturer.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase))
        {
            return "AuthenticAMD";
        }

        if (manufacturer.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return "GenuineIntel";
        }

        return CleanName(manufacturer);
    }

    private static string FormatRevision(uint revision)
    {
        return revision > 0 ? revision.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private static string FormatUInt(uint value)
    {
        return value > 0 ? value.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private static string CollectIntegratedVideo(IEnumerable<VideoControllerSnapshot> videoControllers)
    {
        var integrated = videoControllers
            .Select(g => new
            {
                Name = CleanName(g.Name),
                Pnp = g.PnpDeviceId
            })
            .Where(g => IsUseful(g.Name))
            .Where(g => IsIntegratedGpu(g.Name, g.Pnp))
            .Select(g => g.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return integrated.Count > 0 ? string.Join(" / ", integrated) : string.Empty;
    }

    private static bool IsIntegratedGpu(string name, string pnp)
    {
        return name.Contains("Radeon 780M", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Iris", StringComparison.OrdinalIgnoreCase)
            || name.Contains("UHD Graphics", StringComparison.OrdinalIgnoreCase)
            || pnp.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase);
    }

    private static string CollectMemoryType(IReadOnlyList<MemoryModuleSnapshot> modules)
    {
        if (modules.Count == 0)
        {
            return string.Empty;
        }

        var type = modules.Select(GetMemoryType).FirstOrDefault(IsUseful);
        var speed = modules.Select(m => m.ConfiguredClockSpeed)
            .Concat(modules.Select(m => m.Speed))
            .FirstOrDefault(s => s > 0);

        return JoinUseful(type, speed > 0 ? $"{speed} MHz" : string.Empty);
    }

    private static string CollectMemoryClock(IReadOnlyList<MemoryModuleSnapshot> modules)
    {
        var speed = modules.Select(m => m.ConfiguredClockSpeed)
            .Concat(modules.Select(m => m.Speed))
            .FirstOrDefault(s => s > 0);

        return speed > 0 ? $"{speed} MHz" : string.Empty;
    }

    private static string GetMemoryType(MemoryModuleSnapshot module)
    {
        var smbiosType = module.SmbiosMemoryType;
        if (smbiosType is 20 or 21 or 22 or 24 or 26 or 27 or 28 or 29 or 30 or 31 or 34 or 35)
        {
            return smbiosType switch
            {
                20 => "DDR",
                21 => "DDR2",
                22 => "DDR2 FB-DIMM",
                24 => "DDR3",
                26 => "DDR4",
                27 => "LPDDR",
                28 => "LPDDR2",
                29 => "LPDDR3",
                30 => "LPDDR4",
                31 => "逻辑非易失内存",
                34 => "DDR5",
                35 => "LPDDR5",
                _ => string.Empty
            };
        }

        var memoryType = module.MemoryType;
        return memoryType switch
        {
            20 => "DDR",
            21 => "DDR2",
            24 => "DDR3",
            26 => "DDR4",
            34 => "DDR5",
            _ => string.Empty
        };
    }

    private static string FirstUseful(params string?[] values)
    {
        return values.Select(CleanName).FirstOrDefault(IsUseful) ?? string.Empty;
    }

    private static string JoinUseful(params string?[] values)
    {
        var useful = values.Select(CleanName).Where(IsUseful).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return useful.Count > 0 ? string.Join(' ', useful) : string.Empty;
    }

    private static string CleanName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = Regex.Replace(value, @"\s+", " ").Trim();
        return cleaned.Trim('\0');
    }

    private static bool IsUseful(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return !trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("System Product Name", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase);
    }
}
