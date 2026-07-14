namespace HwScope.Core.Hardware.Storage.Nvme;

internal sealed record NvmeSmartHealthLog(
    byte CriticalWarning,
    ushort CompositeTemperatureKelvin,
    byte AvailableSparePercent,
    byte AvailableSpareThresholdPercent,
    byte PercentageUsed,
    UInt128 DataUnitsRead,
    UInt128 DataUnitsWritten,
    UInt128 HostReadCommands,
    UInt128 HostWriteCommands,
    UInt128 ControllerBusyMinutes,
    UInt128 PowerCycles,
    UInt128 PowerOnHours,
    UInt128 UnsafeShutdowns,
    UInt128 MediaAndDataIntegrityErrors,
    UInt128 ErrorInformationLogEntries,
    uint WarningCompositeTemperatureMinutes,
    uint CriticalCompositeTemperatureMinutes,
    IReadOnlyList<ushort> TemperatureSensorsKelvin,
    byte[] RawBytes);
