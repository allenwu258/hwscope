using HwScope.Core.Hardware.DeviceTopology.Pci;

namespace HwScope.Core.Tests.Hardware.DeviceTopology.Pci;

public sealed class PciTopologyBuilderTests
{
    [Fact]
    public void Build_CreatesHierarchyAndFormatsVerifiedLinkProperties()
    {
        var root = CreateRecord(
            "PCI\\VEN_1022&DEV_14DA\\ROOT",
            parentInstanceId: "ACPI\\PNP0A08\\0",
            name: "PCI Express Root Port",
            deviceType: 8,
            baseClass: 0x06,
            bus: 0,
            address: 2u << 16,
            locationPath: "PCIROOT(0)#PCI(0200)");
        var endpoint = CreateRecord(
            "PCI\\VEN_144D&DEV_A80C&SUBSYS_A801144D&REV_00\\NVME",
            parentInstanceId: root.InstanceId,
            name: "Samsung NVMe Controller",
            deviceType: 2,
            baseClass: 0x01,
            subClass: 0x08,
            programmingInterface: 0x02,
            bus: 3,
            address: 0,
            locationPath: "PCIROOT(0)#PCI(0204)#PCI(0000)",
            currentSpeed: 4,
            currentWidth: 4,
            maximumSpeed: 4,
            maximumWidth: 4,
            currentPayload: 1,
            maximumPayload: 2);

        var snapshot = PciTopologyBuilder.Build(
            [root, endpoint],
            generatedAt: new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));

        var rootNode = Assert.Single(snapshot.Nodes, node => node.Identity.InstanceId == root.InstanceId);
        var endpointNode = Assert.Single(snapshot.Nodes, node => node.Identity.InstanceId == endpoint.InstanceId);
        Assert.Equal(PciTopologyNodeKind.Root, rootNode.Kind);
        Assert.Equal(rootNode.NodeId, endpointNode.ParentNodeId);
        Assert.Contains(endpointNode.NodeId, rootNode.ChildNodeIds);
        Assert.Equal("03:00.0", endpointNode.Address!.ToString());
        Assert.Equal("144D", endpointNode.PciIdentity.VendorId);
        Assert.Equal("A80C", endpointNode.PciIdentity.DeviceId);
        Assert.Equal("PCIe Gen 4 (16.0 GT/s)", endpointNode.Link.CurrentGeneration.DisplayText);
        Assert.Equal("x4", endpointNode.Link.CurrentWidth.DisplayText);
        Assert.Equal((uint)256, endpointNode.Link.CurrentPayloadBytes.Value);
        Assert.Equal((uint)512, endpointNode.Link.MaximumPayloadBytes.Value);
        Assert.Empty(snapshot.Diagnostics.Entries);
    }

    [Fact]
    public void Build_ReportsAddressConflictWithoutReplacingReportedAddress()
    {
        var record = CreateRecord(
            "PCI\\VEN_1234&DEV_5678\\DEVICE",
            bus: 5,
            address: 1u << 16,
            locationPath: "PCIROOT(0)#PCI(0200)");

        var snapshot = PciTopologyBuilder.Build([record]);

        var node = Assert.Single(snapshot.Nodes);
        Assert.Equal(PciTopologyNodeKind.Endpoint, node.Kind);
        Assert.Equal("05:01.0", node.Address!.ToString());
        Assert.Contains(snapshot.Diagnostics.Entries, entry => entry.Code == "pci.address-location-conflict");
    }

    [Fact]
    public void Build_BreaksParentCycleDeterministically()
    {
        var first = CreateRecord("PCI\\A", parentInstanceId: "PCI\\B", name: "A");
        var second = CreateRecord("PCI\\B", parentInstanceId: "PCI\\A", name: "B");

        var snapshot = PciTopologyBuilder.Build([first, second]);

        Assert.Equal(2, snapshot.Nodes.Count);
        Assert.Single(snapshot.RootNodeIds);
        Assert.Contains(snapshot.Diagnostics.Entries, entry => entry.Code == "pci.parent-cycle");
        Assert.Equal(PciTopologyBuilder.BuildNodeId("PCI\\A"), snapshot.RootNodeIds[0]);
    }

    [Fact]
    public void ReportFormatter_ContainsCoordinatesHierarchyAndDiagnostics()
    {
        var record = CreateRecord(
            "PCI\\VEN_1234&DEV_5678\\DEVICE",
            name: "Sample Endpoint",
            bus: 1,
            address: 0,
            currentSpeed: 3,
            currentWidth: 8);
        var snapshot = PciTopologyBuilder.Build(
            [record],
            [new HwScope.Core.Hardware.DeviceTopology.DeviceTopologyDiagnostic(
                HwScope.Core.Hardware.DeviceTopology.DeviceTopologyDiagnosticSeverity.Warning,
                "sample.warning",
                "Sample diagnostic")],
            new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero));

        var text = PciTopologyReportFormatter.Format(snapshot);

        Assert.Contains("[01:00.0] Sample Endpoint", text);
        Assert.Contains("PCIe Gen 3 (8.0 GT/s) x8", text);
        Assert.Contains("sample.warning", text);
        Assert.Contains("2026-07-15", text);
    }

    [Fact]
    public void Collector_ConvertsSourceFailureIntoSnapshotDiagnostic()
    {
        var collector = new PciTopologyCollector(new ThrowingSource());

        var snapshot = collector.Collect();

        Assert.Empty(snapshot.Nodes);
        var diagnostic = Assert.Single(snapshot.Diagnostics.Entries);
        Assert.Equal("pci.enumeration-failed", diagnostic.Code);
        Assert.Contains("InvalidOperationException", diagnostic.Message);
    }

    private static PciDeviceRecord CreateRecord(
        string instanceId,
        string? parentInstanceId = null,
        string name = "PCI Device",
        uint? deviceType = 2,
        uint? baseClass = 0x02,
        uint? subClass = 0,
        uint? programmingInterface = 0,
        uint? bus = null,
        uint? address = null,
        string? locationPath = null,
        uint? currentSpeed = null,
        uint? currentWidth = null,
        uint? maximumSpeed = null,
        uint? maximumWidth = null,
        uint? currentPayload = null,
        uint? maximumPayload = null)
    {
        return new PciDeviceRecord(
            instanceId,
            parentInstanceId,
            name,
            name,
            "Vendor",
            null,
            null,
            [instanceId.Split('\\')[1]],
            [],
            locationPath is null ? [] : [locationPath],
            "sample",
            0,
            null,
            bus,
            address,
            deviceType,
            baseClass,
            subClass,
            programmingInterface,
            currentSpeed,
            currentWidth,
            maximumSpeed,
            maximumWidth,
            currentPayload,
            maximumPayload,
            null,
            null,
            null,
            null,
            null,
            null,
            "Sample Driver",
            "Sample Provider",
            "1.0",
            "sample.inf");
    }

    private sealed class ThrowingSource : IPciDeviceSource
    {
        public PciDeviceSourceResult ReadPresentDevices()
        {
            throw new InvalidOperationException("Synthetic source failure.");
        }
    }
}
