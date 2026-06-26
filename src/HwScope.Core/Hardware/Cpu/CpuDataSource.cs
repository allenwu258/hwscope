namespace HwScope.Core.Hardware.Cpu;

public enum CpuDataSource
{
    Unknown,
    Wmi,
    WindowsApi,
    Cpuid,
    Mapping,
    Computed,
    Placeholder
}
