using HwScope.App.Pages.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Pci;

namespace HwScope.App.Tests.Pages.DeviceTopology;

public sealed class PciDiagnosticTreeProjectionTests
{
    [Fact]
    public void DefaultProjectionExpandsRootsOnly()
    {
        var projection = new PciDiagnosticTreeProjection();
        projection.SetSnapshot(CreateSnapshot());

        var rows = projection.Build(null, PciDiagnosticTreeFilter.All);

        Assert.Equal(["root", "bridge"], rows.Select(row => row.NodeId));
        Assert.Equal([0, 1], rows.Select(row => row.Depth));
    }

    [Fact]
    public void ExpandPathMakesTargetAndAncestorsVisible()
    {
        var projection = new PciDiagnosticTreeProjection();
        projection.SetSnapshot(CreateSnapshot());

        Assert.True(projection.ExpandPath("endpoint"));
        var rows = projection.Build(null, PciDiagnosticTreeFilter.All);

        Assert.Equal(["root", "bridge", "endpoint"], rows.Select(row => row.NodeId));
    }

    [Fact]
    public void SearchAndProblemFilterPreserveExactAncestors()
    {
        var projection = new PciDiagnosticTreeProjection();
        projection.SetSnapshot(CreateSnapshot());

        var searchRows = projection.Build("NVMe", PciDiagnosticTreeFilter.All);
        var problemRows = projection.Build(null, PciDiagnosticTreeFilter.Problems);

        Assert.Equal(["root", "bridge", "endpoint"], searchRows.Select(row => row.NodeId));
        Assert.Equal(["root", "bridge", "endpoint"], problemRows.Select(row => row.NodeId));
        Assert.True(problemRows[^1].HasProblem);
    }

    [Fact]
    public void RawFormatterIncludesIdentitySourcesAndNodeDiagnostics()
    {
        var snapshot = CreateSnapshot();
        var endpoint = Assert.Single(snapshot.Nodes, node => node.NodeId == "endpoint");

        var text = PciDiagnosticNodeFormatter.BuildRawReport(endpoint, snapshot);

        Assert.Contains("PCI Express Node Diagnostic", text);
        Assert.Contains("PCI\\VEN_1234&DEV_5678\\ENDPOINT", text);
        Assert.Contains("pci.test-warning", text);
        Assert.Contains("ConfigurationManager", text);
        Assert.Contains("Overview", text);
        Assert.Contains("Resource Capability Summary", text);
    }

    [Fact]
    public void StandaloneDiagnosticsAppearInProblemFilterAndSummary()
    {
        var snapshot = WithoutEndpointProblem(CreateSnapshot()) with
        {
            Diagnostics = DeviceTopologyDiagnostics.Empty
        };
        DeviceTopologyDiagnostic[] diagnostics =
        [
            new(DeviceTopologyDiagnosticSeverity.Warning, "pci.global-warning", "Global warning."),
            new(DeviceTopologyDiagnosticSeverity.Error, "pci.missing-node", "Missing branch.", "pci:missing"),
            new(DeviceTopologyDiagnosticSeverity.Information, "pci.global-info", "Informational detail.")
        ];
        var projection = new PciDiagnosticTreeProjection();
        projection.SetSnapshot(snapshot, diagnostics);

        var rows = projection.Build(null, PciDiagnosticTreeFilter.Problems);

        Assert.Equal(2, projection.ProblemCount);
        Assert.Equal(3, projection.DiagnosticCount);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.True(row.IsStandaloneDiagnostic));
        Assert.Contains(rows, row => row.Coordinate == "pci.global-warning");
        Assert.Contains(rows, row => row.Coordinate == "pci.missing-node");
    }

    [Fact]
    public void CollapseAllRemainsCollapsedAfterSnapshotRefresh()
    {
        var projection = new PciDiagnosticTreeProjection();
        var snapshot = CreateSnapshot();
        projection.SetSnapshot(snapshot);
        projection.CollapseAll();

        projection.SetSnapshot(snapshot with { GeneratedAt = snapshot.GeneratedAt.AddSeconds(1) });
        var rows = projection.Build(null, PciDiagnosticTreeFilter.All);

        Assert.Equal(["root"], rows.Select(row => row.NodeId));
    }

    [Fact]
    public void NodeDiagnosticCountsAsProblemWithoutPnpProblemCode()
    {
        var snapshot = WithoutEndpointProblem(CreateSnapshot());
        var projection = new PciDiagnosticTreeProjection();
        projection.SetSnapshot(snapshot);

        Assert.Equal(1, projection.ProblemCount);
        var rows = projection.Build(null, PciDiagnosticTreeFilter.Problems);
        Assert.Equal(["root", "bridge", "endpoint"], rows.Select(row => row.NodeId));
        Assert.True(rows[^1].HasProblem);
    }

    [Fact]
    public void SyntheticRootAndUnavailableValuesUseAccurateSources()
    {
        var snapshot = CreateSnapshot();
        var original = Assert.Single(snapshot.Nodes, node => node.NodeId == "root");
        var synthetic = original with
        {
            Address = null,
            RawBusNumber = null,
            RawDeviceAddress = null,
            PciIdentity = new PciIdentity(string.Empty, string.Empty, string.Empty, string.Empty),
            Identity = original.Identity with
            {
                Enumerator = "PCIROOT",
                DisplayName = "PCI Root 0",
                DeviceDescription = "Synthetic PCI root"
            }
        };
        var fields = PciDiagnosticNodeFormatter.BuildOverview(
            synthetic,
            snapshot with { Nodes = [synthetic, .. snapshot.Nodes.Where(node => node.NodeId != "root")] });

        Assert.Equal("Derived", Assert.Single(fields, field => field.Label == "设备名称").Source);
        Assert.Equal("Derived", Assert.Single(fields, field => field.Label == "设备描述").Source);
        Assert.Equal("Unavailable", Assert.Single(fields, field => field.Label == "BDF").Source);
        Assert.Equal("Unavailable", Assert.Single(fields, field => field.Label == "Vendor / Device").Source);
    }

    [Fact]
    public void ResourceTabDeclaresCapabilityOnlyScope()
    {
        var endpoint = Assert.Single(CreateSnapshot().Nodes, node => node.NodeId == "endpoint");

        var scope = Assert.Single(
            PciDiagnosticNodeFormatter.BuildResources(endpoint),
            field => field.Label == "数据范围");

        Assert.Contains("未读取实际", scope.Value);
        Assert.NotNull(scope.Note);
        Assert.Contains("不表示", scope.Note);
    }

    private static PciTopologySnapshot CreateSnapshot()
    {
        var root = Node(
            "root",
            null,
            ["bridge"],
            PciTopologyNodeKind.Root,
            PciDeviceType.PciExpressRootPort,
            "PCI Root",
            new PciAddress(0, 0, 0, 0));
        var bridge = Node(
            "bridge",
            "root",
            ["endpoint"],
            PciTopologyNodeKind.Bridge,
            PciDeviceType.PciExpressDownstreamSwitchPort,
            "PCIe Bridge",
            new PciAddress(0, 1, 0, 0));
        var endpoint = Node(
            "endpoint",
            "bridge",
            [],
            PciTopologyNodeKind.Endpoint,
            PciDeviceType.PciExpressEndpoint,
            "NVMe Controller",
            new PciAddress(0, 2, 0, 0),
            problemCode: 22);
        return new PciTopologySnapshot(
            [root, bridge, endpoint],
            [root.NodeId],
            new DeviceTopologyDiagnostics(
            [
                new DeviceTopologyDiagnostic(
                    DeviceTopologyDiagnosticSeverity.Warning,
                    "pci.test-warning",
                    "Endpoint warning.",
                    endpoint.NodeId)
            ]),
            DateTimeOffset.UnixEpoch);
    }

    private static PciTopologyNode Node(
        string nodeId,
        string? parentNodeId,
        IReadOnlyList<string> children,
        PciTopologyNodeKind kind,
        PciDeviceType deviceType,
        string name,
        PciAddress address,
        uint? problemCode = null)
    {
        return new PciTopologyNode(
            nodeId,
            parentNodeId,
            children,
            new PnpDeviceIdentity(
                nodeId,
                nodeId == "endpoint" ? "PCI\\VEN_1234&DEV_5678\\ENDPOINT" : $"PCI\\{nodeId}",
                name,
                name,
                "Vendor",
                Guid.Parse("11111111-2222-3333-4444-555555555555"),
                null,
                ["PCI\\VEN_1234&DEV_5678"],
                [],
                [$"PCIROOT(0)#PCI({address.Device:X2}{address.Function:X2})"],
                "PCI",
                "pci",
                new DeviceNodeStatus(0, problemCode)),
            kind,
            deviceType,
            address,
            address.Bus,
            ((uint)address.Device << 16) | address.Function,
            new PciIdentity("1234", "5678", "00000000", "01"),
            new PciClassInfo(0x01, 0x08, 0x02, "Non-Volatile Memory Controller"),
            new PciLinkInfo(
                Field(4u, "Gen 4"),
                Field(4u, "x4"),
                Field(4u, "Gen 4"),
                Field(4u, "x4"),
                Field(256u, "256 bytes"),
                Field(512u, "512 bytes"),
                Field(512u, "512 bytes")),
            new PciCapabilityInfo(
                Field(4u, "4"),
                Field(true, "是"),
                Field(1u, "MSI-X"),
                Field(32u, "32"),
                Field(1u, "Memory")),
            new PciDriverInfo("Driver", "Provider", "1.0", "driver.inf", "pci"));
    }

    private static TopologyFieldValue<T> Field<T>(T value, string display)
    {
        return new TopologyFieldValue<T>(
            value,
            display,
            DeviceTopologyDataSource.ConfigurationManager,
            TopologyFieldAvailability.Available);
    }

    private static PciTopologySnapshot WithoutEndpointProblem(PciTopologySnapshot snapshot)
    {
        return snapshot with
        {
            Nodes = snapshot.Nodes.Select(node => node.NodeId == "endpoint"
                    ? node with
                    {
                        Identity = node.Identity with
                        {
                            Status = new DeviceNodeStatus(0, null)
                        }
                    }
                    : node)
                .ToList()
        };
    }
}
