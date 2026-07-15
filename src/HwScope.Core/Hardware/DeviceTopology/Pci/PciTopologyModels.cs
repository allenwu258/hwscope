namespace HwScope.Core.Hardware.DeviceTopology.Pci;

public enum PciTopologyNodeKind
{
    Root,
    Bridge,
    Endpoint,
    Unknown
}

public enum PciDeviceType
{
    PciConventional = 0,
    PciX = 1,
    PciExpressEndpoint = 2,
    PciExpressLegacyEndpoint = 3,
    PciExpressRootComplexIntegratedEndpoint = 4,
    PciExpressTreatedAsPci = 5,
    PciConventionalBridge = 6,
    PciXBridge = 7,
    PciExpressRootPort = 8,
    PciExpressUpstreamSwitchPort = 9,
    PciExpressDownstreamSwitchPort = 10,
    PciExpressToPciXBridge = 11,
    PciXToExpressBridge = 12,
    PciExpressBridgeTreatedAsPci = 13,
    PciExpressEventCollector = 14,
    Unknown = -1
}

public sealed record PciIdentity(
    string VendorId,
    string DeviceId,
    string SubsystemId,
    string RevisionId);

public sealed record PciClassInfo(
    byte? BaseClass,
    byte? SubClass,
    byte? ProgrammingInterface,
    string DisplayName)
{
    public string Code => BaseClass.HasValue
        ? $"{BaseClass.Value:X2}{SubClass.GetValueOrDefault():X2}{ProgrammingInterface.GetValueOrDefault():X2}"
        : string.Empty;
}

public sealed record PciLinkInfo(
    TopologyFieldValue<uint> CurrentGeneration,
    TopologyFieldValue<uint> CurrentWidth,
    TopologyFieldValue<uint> MaximumGeneration,
    TopologyFieldValue<uint> MaximumWidth,
    TopologyFieldValue<uint> CurrentPayloadBytes,
    TopologyFieldValue<uint> MaximumPayloadBytes,
    TopologyFieldValue<uint> MaximumReadRequestBytes);

public sealed record PciCapabilityInfo(
    TopologyFieldValue<uint> ExpressSpecVersion,
    TopologyFieldValue<bool> AerCapabilityPresent,
    TopologyFieldValue<uint> InterruptSupport,
    TopologyFieldValue<uint> InterruptMessageMaximum,
    TopologyFieldValue<uint> BarTypes);

public sealed record PciDriverInfo(
    string Description,
    string Provider,
    string Version,
    string InfPath,
    string Service);

public sealed record PciTopologyNode(
    string NodeId,
    string? ParentNodeId,
    IReadOnlyList<string> ChildNodeIds,
    PnpDeviceIdentity Identity,
    PciTopologyNodeKind Kind,
    PciDeviceType DeviceType,
    PciAddress? Address,
    uint? RawBusNumber,
    uint? RawDeviceAddress,
    PciIdentity PciIdentity,
    PciClassInfo Class,
    PciLinkInfo Link,
    PciCapabilityInfo Capabilities,
    PciDriverInfo Driver);

public sealed record PciTopologySnapshot(
    IReadOnlyList<PciTopologyNode> Nodes,
    IReadOnlyList<string> RootNodeIds,
    DeviceTopologyDiagnostics Diagnostics,
    DateTimeOffset GeneratedAt)
{
    public static PciTopologySnapshot Empty { get; } = new([], [], DeviceTopologyDiagnostics.Empty, DateTimeOffset.MinValue);
}
