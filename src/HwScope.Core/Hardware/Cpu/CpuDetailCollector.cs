using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using HwScope.Core.Windows;

namespace HwScope.Core.Hardware.Cpu;

public sealed class CpuDetailCollector
{
    public CpuDetailReport Collect()
    {
        var processors = Wmi.Query("""
            SELECT Name, Manufacturer, Description, NumberOfCores, NumberOfLogicalProcessors,
                   MaxClockSpeed, CurrentClockSpeed, SocketDesignation, ProcessorId,
                   Architecture, Family, Revision, Stepping
            FROM Win32_Processor
            """).ToList();
        var cpu = processors.FirstOrDefault();
        var board = Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        var bios = Wmi.Query("SELECT SMBIOSBIOSVersion, Version, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
        var memoryModules = Wmi.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, MemoryType FROM Win32_PhysicalMemory").ToList();
        var videoControllers = Wmi.Query("SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController").ToList();
        var perfClock = CollectPerfClockMHz();

        var processorName = CleanName(Wmi.GetString(cpu, "Name"));
        var knownInfo = CpuKnownProcessorCatalog.Match(processorName);
        var notes = new List<CpuDataNote>();

        if (knownInfo is not null)
        {
            notes.Add(new CpuDataNote("部分代号、工艺、TDP、缓存和指令集来自处理器型号映射，后续将由 native CPUID 校验。", CpuDataSource.Mapping));
        }

        notes.Add(new CpuDataNote("Stage 1 使用 WMI 作为主要数据源；Family/Revision/Stepping 不等同于完整 raw CPUID。", CpuDataSource.Wmi));

        var coreCount = SumUInt(processors, "NumberOfCores");
        var logicalCount = SumUInt(processors, "NumberOfLogicalProcessors");
        var currentClock = perfClock ?? Positive(Wmi.GetUInt(cpu, "CurrentClockSpeed"));
        var maxClock = Positive(Wmi.GetUInt(cpu, "MaxClockSpeed"));
        var busClock = EstimateBusClock(currentClock, maxClock);
        var multiplier = currentClock is > 0 && busClock is > 0 ? currentClock / busClock : null;
        var wmiPackage = FirstUseful(Wmi.GetString(cpu, "SocketDesignation"));
        var packageValue = FirstUseful(wmiPackage, knownInfo?.Package);
        var packageSource = IsUseful(wmiPackage) ? CpuDataSource.Wmi : knownInfo is null ? CpuDataSource.Unknown : CpuDataSource.Mapping;
        var packageEstimated = !IsUseful(wmiPackage) && knownInfo is not null;

        return new CpuDetailReport(
            Identity: new CpuIdentity(
                DisplayName: CpuField.Text(knownInfo?.DisplayName ?? ParseDisplayName(processorName), knownInfo is null ? CpuDataSource.Wmi : CpuDataSource.Mapping, isEstimated: knownInfo is not null),
                SpecificationName: CpuField.Text(processorName, CpuDataSource.Wmi),
                Vendor: CpuField.Text(NormalizeVendor(Wmi.GetString(cpu, "Manufacturer")), CpuDataSource.Wmi),
                CodeName: CpuField.Text(knownInfo?.CodeName, knownInfo is null ? CpuDataSource.Unknown : CpuDataSource.Mapping, CpuField.PendingCpuidText, isEstimated: knownInfo is not null)),
            Specification: new CpuSpecification(
                Package: CpuField.Text(packageValue, packageSource, isEstimated: packageEstimated),
                Technology: CpuField.Text(knownInfo?.Technology, knownInfo is null ? CpuDataSource.Unknown : CpuDataSource.Mapping, CpuField.PendingCpuidText, isEstimated: knownInfo is not null),
                Tdp: CpuField.Text(knownInfo?.Tdp, knownInfo is null ? CpuDataSource.Unknown : CpuDataSource.Mapping, CpuField.PendingCpuidText, isEstimated: knownInfo is not null),
                CoreVoltage: CpuField.Placeholder<string>("待接入传感器"),
                Family: CpuField.Text(FormatUInt(Wmi.GetUInt(cpu, "Family")), CpuDataSource.Wmi),
                Model: CpuField.Placeholder<string>(),
                Stepping: CpuField.Text(Wmi.GetString(cpu, "Stepping"), CpuDataSource.Wmi),
                ExtendedFamily: CpuField.Placeholder<string>(),
                ExtendedModel: CpuField.Placeholder<string>(),
                Revision: CpuField.Text(knownInfo?.Revision ?? FormatRevision(Wmi.GetUInt(cpu, "Revision")), knownInfo is null ? CpuDataSource.Wmi : CpuDataSource.Mapping, isEstimated: knownInfo is not null)),
            Topology: new CpuTopology(
                PackageCount: CpuField.Number(processors.Count, CpuDataSource.Wmi),
                CoreCount: CpuField.Number(coreCount, CpuDataSource.Wmi),
                LogicalProcessorCount: CpuField.Number(logicalCount, CpuDataSource.Wmi),
                SmtEnabled: CpuField.Boolean(coreCount > 0 && logicalCount > coreCount, CpuDataSource.Computed, isEstimated: true),
                CpuGroupCount: CpuField.Placeholder<int>("待接入 Windows 拓扑 API"),
                NumaNodeCount: CpuField.Placeholder<int>("待接入 Windows 拓扑 API")),
            Clocks: new CpuClockInfo(
                CurrentMHz: CpuField.MHz(currentClock, perfClock is not null ? CpuDataSource.Wmi : CpuDataSource.Wmi),
                BaseMHz: CpuField.MHz(maxClock, CpuDataSource.Wmi),
                MaxMHz: CpuField.MHz(maxClock, CpuDataSource.Wmi),
                BusMHz: CpuField.MHz(busClock, CpuDataSource.Computed, isEstimated: true, note: "按常见 100 MHz 基准时钟估算。"),
                Multiplier: CpuField.Ratio(multiplier, CpuDataSource.Computed, isEstimated: true)),
            Caches: knownInfo?.Caches ?? CreatePlaceholderCaches(),
            Features: knownInfo?.Features ?? CreatePlaceholderFeatures(),
            Platform: new CpuPlatformContext(
                Motherboard: CpuField.Text(JoinUseful(Wmi.GetString(board, "Manufacturer"), Wmi.GetString(board, "Product")), CpuDataSource.Wmi),
                BiosVersion: CpuField.Text(FirstUseful(Wmi.GetString(bios, "SMBIOSBIOSVersion"), Wmi.GetString(bios, "Version")), CpuDataSource.Wmi),
                Chipset: CpuField.Placeholder<string>(),
                IntegratedVideo: CpuField.Text(CollectIntegratedVideo(videoControllers), CpuDataSource.Wmi, isEstimated: true),
                MemoryType: CpuField.Text(CollectMemoryType(memoryModules), CpuDataSource.Wmi),
                MemoryClock: CpuField.Text(CollectMemoryClock(memoryModules), CpuDataSource.Wmi),
                DramFsbRatio: CpuField.Placeholder<string>()),
            Notes: notes,
            GeneratedAt: DateTimeOffset.Now);
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
        return new CpuCacheInfo(level, name, null, null, null, null, null, CpuDataSource.Placeholder, Note: "待接入 native CPUID 或 Windows 拓扑 API。");
    }

    private static IReadOnlyList<CpuFeature> CreatePlaceholderFeatures()
    {
        return
        [
            new CpuFeature(CpuField.PendingCpuidText, CpuFeatureGroup.Other, IsSupported: false, CpuDataSource.Placeholder)
        ];
    }

    private static double? CollectPerfClockMHz()
    {
        var sample = Wmi.Query("""
            SELECT Name, PercentProcessorPerformance, ProcessorFrequency
            FROM Win32_PerfFormattedData_Counters_ProcessorInformation
            WHERE Name = '_Total'
            """).FirstOrDefault();
        var frequency = Wmi.GetUInt(sample, "ProcessorFrequency");
        return Positive(frequency);
    }

    private static uint SumUInt(IEnumerable<ManagementObject> objects, string propertyName)
    {
        var sum = 0u;
        foreach (var obj in objects)
        {
            sum += Wmi.GetUInt(obj, propertyName);
        }

        return sum;
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

    private static string CollectIntegratedVideo(IEnumerable<ManagementObject> videoControllers)
    {
        var integrated = videoControllers
            .Select(g => new
            {
                Name = CleanName(Wmi.GetString(g, "Name")),
                Pnp = Wmi.GetString(g, "PNPDeviceID")
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

    private static string CollectMemoryType(IReadOnlyList<ManagementObject> modules)
    {
        if (modules.Count == 0)
        {
            return string.Empty;
        }

        var type = modules.Select(GetMemoryType).FirstOrDefault(IsUseful);
        var speed = modules.Select(m => Wmi.GetUInt(m, "ConfiguredClockSpeed"))
            .Concat(modules.Select(m => Wmi.GetUInt(m, "Speed")))
            .FirstOrDefault(s => s > 0);

        return JoinUseful(type, speed > 0 ? $"{speed} MHz" : string.Empty);
    }

    private static string CollectMemoryClock(IReadOnlyList<ManagementObject> modules)
    {
        var speed = modules.Select(m => Wmi.GetUInt(m, "ConfiguredClockSpeed"))
            .Concat(modules.Select(m => Wmi.GetUInt(m, "Speed")))
            .FirstOrDefault(s => s > 0);

        return speed > 0 ? $"{speed} MHz" : string.Empty;
    }

    private static string GetMemoryType(ManagementObject module)
    {
        var smbiosType = Wmi.GetUInt(module, "SMBIOSMemoryType");
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

        var memoryType = Wmi.GetUInt(module, "MemoryType");
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
