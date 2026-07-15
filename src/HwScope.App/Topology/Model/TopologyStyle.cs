namespace HwScope.App.Topology.Model;

public sealed record TopologyStyle(
    string AccentKey,
    bool IsDashed = false,
    double Opacity = 1.0)
{
    public static TopologyStyle Default { get; } = new(TopologyAccentKeys.Default);
}

public static class TopologyAccentKeys
{
    public const string Default = "Default";
    public const string Heuristic = "Heuristic";

    public const string CacheL1Data = "Cache.L1Data";
    public const string CacheL1Instruction = "Cache.L1Instruction";
    public const string CacheL2 = "Cache.L2";
    public const string CacheL3 = "Cache.L3";
    public const string CacheL3VCache = "Cache.L3VCache";

    public const string CorePerformance = "Core.Performance";
    public const string CoreEfficiency = "Core.Efficiency";

    public const string GroupPackage = "Group.Package";
    public const string GroupNuma = "Group.Numa";
    public const string GroupPcieRoot = "Group.PcieRoot";
    public const string DevicePcie = "Device.Pcie";
    public const string GroupUsbController = "Group.UsbController";
    public const string GroupUsbHub = "Group.UsbHub";
    public const string PortUsb = "Port.Usb";
    public const string DeviceUsb = "Device.Usb";
}
