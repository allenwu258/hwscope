namespace HwScope.Core.Hardware.Memory;

public sealed record MemoryDetailReport(
    MemorySummary Summary,
    MemoryRuntimeInfo Runtime,
    IReadOnlyList<MemoryModuleDetail> Modules,
    IReadOnlyList<MemoryDataNote> Notes,
    DateTimeOffset GeneratedAt);

public sealed record MemorySummary(
    MemoryFieldValue<string> Type,
    MemoryFieldValue<string> TotalCapacity,
    MemoryFieldValue<int> ModuleCount,
    MemoryFieldValue<string> Layout,
    MemoryFieldValue<string> ConfiguredSpeed,
    MemoryFieldValue<string> ChannelMode);

public sealed record MemoryRuntimeInfo(
    MemoryFieldValue<double> ClockMHz,
    MemoryFieldValue<string> EffectiveRate,
    MemoryFieldValue<string> Ratio,
    MemoryTimingValues PrimaryTimings);

public sealed record MemoryTimingValues(
    MemoryFieldValue<string> CasLatency,
    MemoryFieldValue<string> Trcd,
    MemoryFieldValue<string> Trp,
    MemoryFieldValue<string> Tras,
    MemoryFieldValue<string> Trc,
    MemoryFieldValue<string> CommandRate);

public sealed record MemoryModuleDetail(
    string Id,
    MemoryModuleIdentity Identity,
    MemoryModuleOrganization Organization,
    MemoryModuleVoltages Voltages,
    IReadOnlyList<MemoryTimingProfile> TimingProfiles,
    IReadOnlyList<MemoryModuleFeature> Features,
    IReadOnlyList<MemoryDataNote> Notes);

public sealed record MemoryModuleIdentity(
    MemoryFieldValue<string> Slot,
    MemoryFieldValue<string> DisplayName,
    MemoryFieldValue<string> Capacity,
    MemoryFieldValue<string> ModuleType,
    MemoryFieldValue<string> MemoryType,
    MemoryFieldValue<string> MaxBandwidth,
    MemoryFieldValue<string> Manufacturer,
    MemoryFieldValue<string> DramManufacturer,
    MemoryFieldValue<string> PartNumber,
    MemoryFieldValue<string> SerialNumber,
    MemoryFieldValue<string> ManufacturingDate,
    MemoryFieldValue<string> Revision);

public sealed record MemoryModuleOrganization(
    MemoryFieldValue<string> RankMix,
    MemoryFieldValue<int> RankCount,
    MemoryFieldValue<int> BankGroupCount,
    MemoryFieldValue<int> BanksPerGroup,
    MemoryFieldValue<int> RowAddressBits,
    MemoryFieldValue<int> ColumnAddressBits,
    MemoryFieldValue<string> DeviceWidth,
    MemoryFieldValue<string> BusWidth,
    MemoryFieldValue<string> DataWidth,
    MemoryFieldValue<string> TotalWidth,
    MemoryFieldValue<string> Ecc,
    MemoryFieldValue<string> OnDieEcc);

public sealed record MemoryModuleVoltages(
    MemoryFieldValue<string> ConfiguredVoltage,
    MemoryFieldValue<string> MinVoltage,
    MemoryFieldValue<string> MaxVoltage,
    MemoryFieldValue<string> Vdd,
    MemoryFieldValue<string> Vddq,
    MemoryFieldValue<string> Vpp);

public sealed record MemoryTimingProfile(
    string Name,
    MemoryFieldValue<string> Frequency,
    MemoryFieldValue<string> EffectiveRate,
    MemoryFieldValue<string> CasLatency,
    MemoryFieldValue<string> Trcd,
    MemoryFieldValue<string> Trp,
    MemoryFieldValue<string> Tras,
    MemoryFieldValue<string> Trc,
    MemoryFieldValue<string> Voltage,
    MemoryDataSource Source);

public sealed record MemoryModuleFeature(
    string Name,
    MemoryFieldValue<string> Value,
    MemoryDataSource Source);

public sealed record MemoryDataNote(string Message, MemoryDataSource Source);
