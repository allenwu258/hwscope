using HwScope.Core.Windows.Devices;

namespace HwScope.Core.Hardware.DeviceTopology.Pci;

internal sealed class PciSetupApiDeviceSource : IPciDeviceSource
{
    private static readonly Guid DevicePropertyNamespace = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid DeviceRelationsNamespace = new("4340a6c5-93fa-4706-972c-7b648008a5a7");
    private static readonly Guid DeviceContainerNamespace = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c");
    private static readonly Guid DeviceDriverNamespace = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");
    private static readonly Guid PciDeviceNamespace = new("3ab22e31-8264-4b4e-9af5-a8d2d8e33e62");

    private static readonly DevicePropertyKey DeviceDescription = Device(2);
    private static readonly DevicePropertyKey HardwareIds = Device(3);
    private static readonly DevicePropertyKey CompatibleIds = Device(4);
    private static readonly DevicePropertyKey Service = Device(6);
    private static readonly DevicePropertyKey ClassGuid = Device(10);
    private static readonly DevicePropertyKey Manufacturer = Device(13);
    private static readonly DevicePropertyKey FriendlyName = Device(14);
    private static readonly DevicePropertyKey BusNumber = Device(23);
    private static readonly DevicePropertyKey Address = Device(30);
    private static readonly DevicePropertyKey LocationPaths = Device(37);
    private static readonly DevicePropertyKey DevNodeStatus = new(DeviceRelationsNamespace, 2);
    private static readonly DevicePropertyKey ProblemCode = new(DeviceRelationsNamespace, 3);
    private static readonly DevicePropertyKey Parent = new(DeviceRelationsNamespace, 8);
    private static readonly DevicePropertyKey ContainerId = new(DeviceContainerNamespace, 2);
    private static readonly DevicePropertyKey DriverVersion = Driver(3);
    private static readonly DevicePropertyKey DriverDescription = Driver(4);
    private static readonly DevicePropertyKey DriverInfPath = Driver(5);
    private static readonly DevicePropertyKey DriverProvider = Driver(9);
    private static readonly DevicePropertyKey PciDeviceType = Pci(1);
    private static readonly DevicePropertyKey PciBaseClass = Pci(3);
    private static readonly DevicePropertyKey PciSubClass = Pci(4);
    private static readonly DevicePropertyKey PciProgrammingInterface = Pci(5);
    private static readonly DevicePropertyKey PciCurrentPayloadSize = Pci(6);
    private static readonly DevicePropertyKey PciMaximumPayloadSize = Pci(7);
    private static readonly DevicePropertyKey PciMaximumReadRequestSize = Pci(8);
    private static readonly DevicePropertyKey PciCurrentLinkSpeed = Pci(9);
    private static readonly DevicePropertyKey PciCurrentLinkWidth = Pci(10);
    private static readonly DevicePropertyKey PciMaximumLinkSpeed = Pci(11);
    private static readonly DevicePropertyKey PciMaximumLinkWidth = Pci(12);
    private static readonly DevicePropertyKey PciExpressSpecVersion = Pci(13);
    private static readonly DevicePropertyKey PciInterruptSupport = Pci(14);
    private static readonly DevicePropertyKey PciInterruptMessageMaximum = Pci(15);
    private static readonly DevicePropertyKey PciBarTypes = Pci(16);
    private static readonly DevicePropertyKey PciAerCapabilityPresent = Pci(17);

    private static readonly IReadOnlyList<DevicePropertyRequest> Requests =
    [
        Request(nameof(DeviceDescription), DeviceDescription),
        Request(nameof(HardwareIds), HardwareIds),
        Request(nameof(CompatibleIds), CompatibleIds),
        Request(nameof(Service), Service),
        Request(nameof(ClassGuid), ClassGuid),
        Request(nameof(Manufacturer), Manufacturer),
        Request(nameof(FriendlyName), FriendlyName),
        Request(nameof(BusNumber), BusNumber),
        Request(nameof(Address), Address),
        Request(nameof(LocationPaths), LocationPaths),
        Request(nameof(DevNodeStatus), DevNodeStatus),
        Request(nameof(ProblemCode), ProblemCode),
        Request(nameof(Parent), Parent),
        Request(nameof(ContainerId), ContainerId),
        Request(nameof(DriverVersion), DriverVersion),
        Request(nameof(DriverDescription), DriverDescription),
        Request(nameof(DriverInfPath), DriverInfPath),
        Request(nameof(DriverProvider), DriverProvider),
        Request(nameof(PciDeviceType), PciDeviceType),
        Request(nameof(PciBaseClass), PciBaseClass),
        Request(nameof(PciSubClass), PciSubClass),
        Request(nameof(PciProgrammingInterface), PciProgrammingInterface),
        Request(nameof(PciCurrentPayloadSize), PciCurrentPayloadSize),
        Request(nameof(PciMaximumPayloadSize), PciMaximumPayloadSize),
        Request(nameof(PciMaximumReadRequestSize), PciMaximumReadRequestSize),
        Request(nameof(PciCurrentLinkSpeed), PciCurrentLinkSpeed),
        Request(nameof(PciCurrentLinkWidth), PciCurrentLinkWidth),
        Request(nameof(PciMaximumLinkSpeed), PciMaximumLinkSpeed),
        Request(nameof(PciMaximumLinkWidth), PciMaximumLinkWidth),
        Request(nameof(PciExpressSpecVersion), PciExpressSpecVersion),
        Request(nameof(PciInterruptSupport), PciInterruptSupport),
        Request(nameof(PciInterruptMessageMaximum), PciInterruptMessageMaximum),
        Request(nameof(PciBarTypes), PciBarTypes),
        Request(nameof(PciAerCapabilityPresent), PciAerCapabilityPresent)
    ];

    private readonly SetupApiDeviceEnumerator _enumerator = new();

    public PciDeviceSourceResult ReadPresentDevices()
    {
        var result = _enumerator.EnumeratePresentDevices("PCI", Requests);
        var devices = result.Devices.Select(Convert).ToList();
        var diagnostics = result.Diagnostics.Select(diagnostic => new DeviceTopologyDiagnostic(
            DeviceTopologyDiagnosticSeverity.Warning,
            diagnostic.Code,
            string.IsNullOrWhiteSpace(diagnostic.PropertyName)
                ? diagnostic.Message
                : $"{diagnostic.PropertyName}: {diagnostic.Message}",
            string.IsNullOrWhiteSpace(diagnostic.InstanceId) ? null : PciTopologyBuilder.BuildNodeId(diagnostic.InstanceId))).ToList();
        return new PciDeviceSourceResult(devices, diagnostics);
    }

    private static PciDeviceRecord Convert(NativeDeviceInfo device)
    {
        var properties = device.Properties;
        var description = DevicePropertyValueReader.GetString(properties, DeviceDescription) ?? string.Empty;
        return new PciDeviceRecord(
            device.InstanceId,
            DevicePropertyValueReader.GetString(properties, Parent),
            DevicePropertyValueReader.GetString(properties, FriendlyName) ?? description,
            description,
            DevicePropertyValueReader.GetString(properties, Manufacturer) ?? string.Empty,
            DevicePropertyValueReader.GetGuid(properties, ClassGuid),
            DevicePropertyValueReader.GetGuid(properties, ContainerId),
            DevicePropertyValueReader.GetStringList(properties, HardwareIds),
            DevicePropertyValueReader.GetStringList(properties, CompatibleIds),
            DevicePropertyValueReader.GetStringList(properties, LocationPaths),
            DevicePropertyValueReader.GetString(properties, Service) ?? string.Empty,
            DevicePropertyValueReader.GetUInt32(properties, DevNodeStatus) ?? 0,
            DevicePropertyValueReader.GetUInt32(properties, ProblemCode),
            DevicePropertyValueReader.GetUInt32(properties, BusNumber),
            DevicePropertyValueReader.GetUInt32(properties, Address),
            DevicePropertyValueReader.GetUInt32(properties, PciDeviceType),
            DevicePropertyValueReader.GetUInt32(properties, PciBaseClass),
            DevicePropertyValueReader.GetUInt32(properties, PciSubClass),
            DevicePropertyValueReader.GetUInt32(properties, PciProgrammingInterface),
            DevicePropertyValueReader.GetUInt32(properties, PciCurrentLinkSpeed),
            DevicePropertyValueReader.GetUInt32(properties, PciCurrentLinkWidth),
            DevicePropertyValueReader.GetUInt32(properties, PciMaximumLinkSpeed),
            DevicePropertyValueReader.GetUInt32(properties, PciMaximumLinkWidth),
            DevicePropertyValueReader.GetUInt32(properties, PciCurrentPayloadSize),
            DevicePropertyValueReader.GetUInt32(properties, PciMaximumPayloadSize),
            DevicePropertyValueReader.GetUInt32(properties, PciMaximumReadRequestSize),
            DevicePropertyValueReader.GetUInt32(properties, PciExpressSpecVersion),
            DevicePropertyValueReader.GetBoolean(properties, PciAerCapabilityPresent),
            DevicePropertyValueReader.GetUInt32(properties, PciInterruptSupport),
            DevicePropertyValueReader.GetUInt32(properties, PciInterruptMessageMaximum),
            DevicePropertyValueReader.GetUInt32(properties, PciBarTypes),
            DevicePropertyValueReader.GetString(properties, DriverDescription) ?? string.Empty,
            DevicePropertyValueReader.GetString(properties, DriverProvider) ?? string.Empty,
            DevicePropertyValueReader.GetString(properties, DriverVersion) ?? string.Empty,
            DevicePropertyValueReader.GetString(properties, DriverInfPath) ?? string.Empty);
    }

    private static DevicePropertyRequest Request(string name, DevicePropertyKey key) => new(name, key);

    private static DevicePropertyKey Device(uint propertyId) => new(DevicePropertyNamespace, propertyId);

    private static DevicePropertyKey Driver(uint propertyId) => new(DeviceDriverNamespace, propertyId);

    private static DevicePropertyKey Pci(uint propertyId) => new(PciDeviceNamespace, propertyId);
}
