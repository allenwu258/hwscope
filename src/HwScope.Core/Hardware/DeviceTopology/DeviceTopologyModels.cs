namespace HwScope.Core.Hardware.DeviceTopology;

public enum DeviceTopologyDataSource
{
    Unknown,
    SetupApi,
    ConfigurationManager,
    PciBusDriver,
    Derived
}

public enum TopologyFieldAvailability
{
    Available,
    Unavailable,
    Unsupported,
    Error
}

public sealed record TopologyFieldValue<T>(
    T? Value,
    string DisplayText,
    DeviceTopologyDataSource Source,
    TopologyFieldAvailability Availability,
    bool IsDerived = false,
    string? Note = null)
{
    public bool IsAvailable => Availability == TopologyFieldAvailability.Available;

    public static TopologyFieldValue<T> Unavailable(string text = "未报告", string? note = null)
    {
        return new TopologyFieldValue<T>(
            default,
            text,
            DeviceTopologyDataSource.Unknown,
            TopologyFieldAvailability.Unavailable,
            Note: note);
    }
}

public enum DeviceTopologyDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record DeviceTopologyDiagnostic(
    DeviceTopologyDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? NodeId = null);

public sealed record DeviceTopologyDiagnostics(IReadOnlyList<DeviceTopologyDiagnostic> Entries)
{
    public static DeviceTopologyDiagnostics Empty { get; } = new([]);

    public bool HasErrors => Entries.Any(entry => entry.Severity == DeviceTopologyDiagnosticSeverity.Error);
}

public sealed record DeviceNodeStatus(
    uint RawStatus,
    uint? ProblemCode)
{
    public bool HasProblem => ProblemCode is > 0;
}

public sealed record PnpDeviceIdentity(
    string StableId,
    string InstanceId,
    string DisplayName,
    string DeviceDescription,
    string Manufacturer,
    Guid? ClassGuid,
    Guid? ContainerId,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    IReadOnlyList<string> LocationPaths,
    string Enumerator,
    string Service,
    DeviceNodeStatus Status);
