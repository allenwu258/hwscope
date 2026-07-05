using System.Globalization;
using System.Text.RegularExpressions;
using HwScope.Core.Hardware.Inventory;

namespace HwScope.Core.Hardware.Memory;

public sealed class MemoryDetailCollector
{
    public MemoryDetailReport Collect()
    {
        return CreateReport(new HardwareInventoryCollector().Collect());
    }

    public MemoryDetailReport CreateReport(HardwareInventorySnapshot snapshot)
    {
        var modules = snapshot.MemoryModules;
        var notes = BuildNotes(snapshot);

        return new MemoryDetailReport(
            BuildSummary(modules),
            BuildRuntime(modules),
            modules.Select(BuildModule).ToList(),
            notes,
            snapshot.GeneratedAt);
    }

    private static MemorySummary BuildSummary(IReadOnlyList<MemoryModuleSnapshot> modules)
    {
        var totalCapacity = modules.Aggregate(0UL, (sum, module) => sum + module.Capacity);
        var type = MostCommon(modules.Select(module => MemoryTypeFormatter.FormatMemoryType(module.SmbiosMemoryType, module.MemoryType)));
        var configuredSpeeds = modules
            .Select(GetConfiguredSpeed)
            .Where(speed => speed > 0)
            .Distinct()
            .OrderByDescending(speed => speed)
            .ToList();

        return new MemorySummary(
            Type: MemoryField.Text(type, MemoryDataSource.Wmi),
            TotalCapacity: MemoryField.Bytes(totalCapacity, MemoryDataSource.Wmi),
            ModuleCount: MemoryField.Number(modules.Count, MemoryDataSource.Wmi),
            Layout: MemoryField.Text(FormatLayout(modules), MemoryDataSource.Computed, isEstimated: true),
            ConfiguredSpeed: MemoryField.Text(FormatSpeedList(configuredSpeeds), MemoryDataSource.Wmi),
            ChannelMode: MemoryField.Placeholder<string>(MemoryField.PendingControllerText));
    }

    private static MemoryRuntimeInfo BuildRuntime(IReadOnlyList<MemoryModuleSnapshot> modules)
    {
        var configuredSpeeds = modules
            .Select(GetConfiguredSpeed)
            .Where(speed => speed > 0)
            .Distinct()
            .OrderByDescending(speed => speed)
            .ToList();

        return new MemoryRuntimeInfo(
            ClockMHz: MemoryField.Placeholder<double>(MemoryField.PendingControllerText),
            EffectiveRate: MemoryField.Text(FormatSpeedList(configuredSpeeds), MemoryDataSource.Wmi),
            Ratio: MemoryField.Placeholder<string>(MemoryField.PendingControllerText),
            PrimaryTimings: new MemoryTimingValues(
                MemoryField.Placeholder<string>(MemoryField.PendingControllerText),
                MemoryField.Placeholder<string>(MemoryField.PendingControllerText),
                MemoryField.Placeholder<string>(MemoryField.PendingControllerText),
                MemoryField.Placeholder<string>(MemoryField.PendingControllerText),
                MemoryField.Placeholder<string>(MemoryField.PendingControllerText),
                MemoryField.Placeholder<string>(MemoryField.PendingControllerText)));
    }

    private static MemoryModuleDetail BuildModule(MemoryModuleSnapshot module, int index)
    {
        var memoryType = MemoryTypeFormatter.FormatMemoryType(module.SmbiosMemoryType, module.MemoryType);
        var formFactor = MemoryTypeFormatter.FormatFormFactor(module.FormFactor);
        var speed = GetConfiguredSpeed(module);
        var slot = FirstUseful(module.DeviceLocator, module.BankLabel, $"Slot {index + 1}");
        var partNumber = CleanName(module.PartNumber);
        var manufacturer = CleanName(module.Manufacturer);
        var displayName = JoinUseful(manufacturer, partNumber);
        var capacity = MemoryField.FormatBytes(module.Capacity);
        var maxBandwidth = speed > 0 ? $"{memoryType}-{speed} ({speed / 2.0:0.#} MHz)" : string.Empty;

        return new MemoryModuleDetail(
            Id: BuildModuleId(module, index),
            Identity: new MemoryModuleIdentity(
                Slot: MemoryField.Text(slot, MemoryDataSource.Wmi),
                DisplayName: MemoryField.Text(FirstUseful(displayName, $"{capacity} {memoryType}"), MemoryDataSource.Wmi),
                Capacity: MemoryField.Bytes(module.Capacity, MemoryDataSource.Wmi),
                ModuleType: MemoryField.Text(formFactor, MemoryDataSource.Wmi),
                MemoryType: MemoryField.Text(memoryType, MemoryDataSource.Wmi),
                MaxBandwidth: MemoryField.Text(maxBandwidth, MemoryDataSource.Wmi),
                Manufacturer: MemoryField.Text(manufacturer, MemoryDataSource.Wmi),
                DramManufacturer: MemoryField.Placeholder<string>(MemoryField.PendingSpdText),
                PartNumber: MemoryField.Text(partNumber, MemoryDataSource.Wmi),
                SerialNumber: MemoryField.Text(module.SerialNumber, MemoryDataSource.Wmi),
                ManufacturingDate: MemoryField.Placeholder<string>(MemoryField.PendingSpdText),
                Revision: MemoryField.Placeholder<string>(MemoryField.PendingSpdText)),
            Organization: new MemoryModuleOrganization(
                RankMix: MemoryField.Placeholder<string>(MemoryField.PendingSpdText),
                RankCount: MemoryField.Placeholder<int>(MemoryField.PendingSpdText),
                BankGroupCount: MemoryField.Placeholder<int>(MemoryField.PendingSpdText),
                BanksPerGroup: MemoryField.Placeholder<int>(MemoryField.PendingSpdText),
                RowAddressBits: MemoryField.Placeholder<int>(MemoryField.PendingSpdText),
                ColumnAddressBits: MemoryField.Placeholder<int>(MemoryField.PendingSpdText),
                DeviceWidth: MemoryField.Placeholder<string>(MemoryField.PendingSpdText),
                BusWidth: FormatBits(module.TotalWidth > 0 ? module.TotalWidth : module.DataWidth, MemoryDataSource.Wmi),
                DataWidth: FormatBits(module.DataWidth, MemoryDataSource.Wmi),
                TotalWidth: FormatBits(module.TotalWidth, MemoryDataSource.Wmi),
                Ecc: FormatEcc(module),
                OnDieEcc: MemoryField.Placeholder<string>(MemoryField.PendingSpdText)),
            Voltages: new MemoryModuleVoltages(
                ConfiguredVoltage: MemoryField.Millivolts(module.ConfiguredVoltage, MemoryDataSource.Wmi),
                MinVoltage: MemoryField.Millivolts(module.MinVoltage, MemoryDataSource.Wmi),
                MaxVoltage: MemoryField.Millivolts(module.MaxVoltage, MemoryDataSource.Wmi),
                Vdd: MemoryField.Placeholder<string>(MemoryField.PendingSpdText),
                Vddq: MemoryField.Placeholder<string>(MemoryField.PendingSpdText),
                Vpp: MemoryField.Placeholder<string>(MemoryField.PendingSpdText)),
            TimingProfiles: [CreatePendingTimingProfile()],
            Features:
            [
                new MemoryModuleFeature("Write Temperature Sense", MemoryField.Placeholder<string>(MemoryField.PendingSpdText), MemoryDataSource.Placeholder),
                new MemoryModuleFeature("Bounded Fault", MemoryField.Placeholder<string>(MemoryField.PendingSpdText), MemoryDataSource.Placeholder),
                new MemoryModuleFeature("BL32", MemoryField.Placeholder<string>(MemoryField.PendingSpdText), MemoryDataSource.Placeholder),
                new MemoryModuleFeature("Non-Standard Core Timings", MemoryField.Placeholder<string>(MemoryField.PendingSpdText), MemoryDataSource.Placeholder)
            ],
            Notes: BuildModuleNotes(module));
    }

    private static MemoryTimingProfile CreatePendingTimingProfile()
    {
        return new MemoryTimingProfile(
            "SPD Profiles",
            MemoryField.Placeholder<string>(MemoryField.PendingSpdText),
            MemoryField.Placeholder<string>(MemoryField.PendingSpdText),
            MemoryField.Placeholder<string>(MemoryField.PendingSpdText),
            MemoryField.Placeholder<string>(MemoryField.PendingSpdText),
            MemoryField.Placeholder<string>(MemoryField.PendingSpdText),
            MemoryField.Placeholder<string>(MemoryField.PendingSpdText),
            MemoryField.Placeholder<string>(MemoryField.PendingSpdText),
            MemoryField.Placeholder<string>(MemoryField.PendingSpdText),
            MemoryDataSource.Placeholder);
    }

    private static IReadOnlyList<MemoryDataNote> BuildNotes(HardwareInventorySnapshot snapshot)
    {
        var notes = new List<MemoryDataNote>
        {
            new("内存模块信息来自 Windows Win32_PhysicalMemory / SMBIOS，字段质量取决于主板固件。", MemoryDataSource.Wmi),
            new("JEDEC/XMP/EXPO 时序需要后续 SPD 读取器；当前版本不会伪造这些字段。", MemoryDataSource.Placeholder),
            new("当前频率、通道模式和 CL/tRCD/tRP/tRAS 需要后续内存控制器读取器。", MemoryDataSource.Placeholder)
        };

        var memoryStep = snapshot.Diagnostics.Steps.FirstOrDefault(step => step.Name == "memory");
        if (memoryStep is not null && memoryStep.Status != HardwareInventoryStepStatus.Success)
        {
            notes.Add(new MemoryDataNote($"内存采集步骤状态：{memoryStep.Status}。{memoryStep.Message}", MemoryDataSource.Wmi));
        }

        var modules = snapshot.MemoryModules;
        if (modules.Count == 0)
        {
            notes.Add(new MemoryDataNote("Windows WMI 没有返回 Win32_PhysicalMemory 数据。", MemoryDataSource.Wmi));
            return notes;
        }

        AddMismatchNote(notes, modules.Select(module => module.Capacity).Where(value => value > 0).Select(value => MemoryField.FormatBytes(value)), "检测到不同容量的内存模块。");
        AddMismatchNote(notes, modules.Select(GetConfiguredSpeed).Where(value => value > 0).Select(value => $"{value} MT/s"), "检测到不同配置速率的内存模块。");
        AddMismatchNote(notes, modules.Select(module => MemoryTypeFormatter.FormatMemoryType(module.SmbiosMemoryType, module.MemoryType)), "检测到不同类型的内存模块。");
        AddMismatchNote(notes, modules.Select(module => CleanName(module.PartNumber)), "检测到不同 Part Number 的内存模块，请确认是否为有意混插。");

        return notes;
    }

    private static IReadOnlyList<MemoryDataNote> BuildModuleNotes(MemoryModuleSnapshot module)
    {
        var missing = new List<string>();
        if (!IsUseful(module.Manufacturer))
        {
            missing.Add("Manufacturer");
        }

        if (!IsUseful(module.PartNumber))
        {
            missing.Add("PartNumber");
        }

        if (!IsUseful(module.SerialNumber))
        {
            missing.Add("SerialNumber");
        }

        if (missing.Count == 0)
        {
            return [];
        }

        return [new MemoryDataNote($"该模块 WMI 缺失字段：{string.Join(", ", missing)}。", MemoryDataSource.Wmi)];
    }

    private static void AddMismatchNote(List<MemoryDataNote> notes, IEnumerable<string> values, string message)
    {
        var unique = values.Select(CleanName).Where(IsUseful).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unique.Count > 1)
        {
            notes.Add(new MemoryDataNote(message, MemoryDataSource.Computed));
        }
    }

    private static MemoryFieldValue<string> FormatBits(uint bits, MemoryDataSource source)
    {
        return bits > 0
            ? MemoryField.Text($"{bits} bit", source)
            : MemoryField.Text(string.Empty, source);
    }

    private static MemoryFieldValue<string> FormatEcc(MemoryModuleSnapshot module)
    {
        if (module.TotalWidth > module.DataWidth && module.DataWidth > 0)
        {
            return MemoryField.Text("可能支持 ECC / 校验位", MemoryDataSource.Computed, isEstimated: true);
        }

        if (module.TotalWidth > 0 && module.DataWidth > 0)
        {
            return MemoryField.Text("无", MemoryDataSource.Computed, isEstimated: true);
        }

        return MemoryField.Placeholder<string>(MemoryField.PendingSpdText);
    }

    private static uint GetConfiguredSpeed(MemoryModuleSnapshot module)
    {
        return module.ConfiguredClockSpeed > 0 ? module.ConfiguredClockSpeed : module.Speed;
    }

    private static string FormatSpeedList(IReadOnlyList<uint> speeds)
    {
        return speeds.Count switch
        {
            0 => string.Empty,
            1 => $"{speeds[0]} MT/s",
            _ => string.Join(" / ", speeds.Select(speed => $"{speed} MT/s"))
        };
    }

    private static string FormatLayout(IReadOnlyList<MemoryModuleSnapshot> modules)
    {
        var capacities = modules.Select(module => module.Capacity).Where(capacity => capacity > 0).ToList();
        if (capacities.Count == 0)
        {
            return string.Empty;
        }

        var groups = capacities
            .GroupBy(capacity => capacity)
            .OrderByDescending(group => group.Key)
            .Select(group => group.Count() > 1
                ? $"{group.Count()} x {MemoryField.FormatBytes(group.Key)}"
                : MemoryField.FormatBytes(group.Key))
            .ToList();

        return string.Join(" + ", groups);
    }

    private static string BuildModuleId(MemoryModuleSnapshot module, int index)
    {
        var raw = FirstUseful(
            JoinUseful(module.DeviceLocator, module.BankLabel, module.SerialNumber, module.PartNumber),
            module.Tag,
            $"slot-index-{index}");
        var normalized = Regex.Replace(raw.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? $"slot-index-{index}" : normalized;
    }

    private static string MostCommon(IEnumerable<string> values)
    {
        return values
            .Select(CleanName)
            .Where(IsUseful)
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string FirstUseful(params string?[] values)
    {
        return values.Select(CleanName).FirstOrDefault(IsUseful) ?? string.Empty;
    }

    private static string JoinUseful(params string?[] values)
    {
        var useful = values.Select(CleanName).Where(IsUseful).ToList();
        return useful.Count > 0 ? string.Join(' ', useful) : string.Empty;
    }

    private static string CleanName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Regex.Replace(value, @"\s+", " ").Trim().Trim('\0');
    }

    private static bool IsUseful(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        return !trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("None", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("System Product Name", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("System manufacturer", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Equals("00000000", StringComparison.OrdinalIgnoreCase);
    }
}
