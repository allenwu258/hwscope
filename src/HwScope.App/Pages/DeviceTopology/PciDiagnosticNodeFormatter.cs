using System.Globalization;
using System.Text;
using HwScope.Core.Hardware.DeviceTopology;
using HwScope.Core.Hardware.DeviceTopology.Pci;

namespace HwScope.App.Pages.DeviceTopology;

internal sealed record PciDiagnosticFieldView(
    string Label,
    string Value,
    string Source,
    string? Note = null);

internal static class PciDiagnosticNodeFormatter
{
    public static IReadOnlyList<PciDiagnosticFieldView> BuildOverview(
        PciTopologyNode node,
        PciTopologySnapshot snapshot)
    {
        var parent = node.ParentNodeId is null
            ? null
            : snapshot.Nodes.FirstOrDefault(candidate =>
                string.Equals(candidate.NodeId, node.ParentNodeId, StringComparison.OrdinalIgnoreCase));
        return
        [
            Reported("节点类型", node.DeviceType.ToString(), "PCI bus / derived"),
            Reported("拓扑角色", node.Kind.ToString(), "Derived"),
            Reported("BDF", node.Address?.ToString() ?? "未报告", "PCI bus / Location Path"),
            Reported("设备名称", node.Identity.DisplayName, "SetupAPI"),
            Reported("设备描述", Empty(node.Identity.DeviceDescription), "SetupAPI"),
            Reported("Vendor / Device", JoinIdentity(node.PciIdentity.VendorId, node.PciIdentity.DeviceId), "Hardware ID"),
            Reported("Subsystem", Empty(node.PciIdentity.SubsystemId), "Hardware ID"),
            Reported("Revision", Empty(node.PciIdentity.RevisionId), "Hardware ID"),
            Reported("Class", $"{Empty(node.Class.Code)} · {node.Class.DisplayName}", "PCI class property"),
            Reported("上游节点", parent is null ? "无" : $"{parent.Address?.ToString() ?? "--"} · {parent.Identity.DisplayName}", "Derived"),
            Reported("下游节点数", node.ChildNodeIds.Count.ToString(CultureInfo.InvariantCulture), "Derived"),
            Reported("设备状态", node.Identity.Status.HasProblem ? $"Problem {node.Identity.Status.ProblemCode}" : "正常", "Configuration Manager"),
            Reported("Instance ID", Empty(node.Identity.InstanceId), "Configuration Manager"),
            Reported("Location Path", node.Identity.LocationPaths.FirstOrDefault() ?? "未报告", "SetupAPI")
        ];
    }

    public static IReadOnlyList<PciDiagnosticFieldView> BuildLinkAndCapabilities(PciTopologyNode node)
    {
        return
        [
            Field("当前链路代际", node.Link.CurrentGeneration),
            Field("当前链路宽度", node.Link.CurrentWidth),
            Field("最大链路代际", node.Link.MaximumGeneration),
            Field("最大链路宽度", node.Link.MaximumWidth),
            Field("当前 Payload", node.Link.CurrentPayloadBytes),
            Field("最大 Payload", node.Link.MaximumPayloadBytes),
            Field("最大 Read Request", node.Link.MaximumReadRequestBytes),
            Field("PCIe 规范版本", node.Capabilities.ExpressSpecVersion),
            Field("AER Capability", node.Capabilities.AerCapabilityPresent),
            Field("Interrupt Support", node.Capabilities.InterruptSupport),
            Field("Interrupt Message Maximum", node.Capabilities.InterruptMessageMaximum),
            Field("BAR Types", node.Capabilities.BarTypes)
        ];
    }

    public static IReadOnlyList<PciDiagnosticFieldView> BuildResources(PciTopologyNode node)
    {
        return
        [
            Reported("Raw BusNumber", FormatRaw(node.RawBusNumber), "DEVPKEY_Device_BusNumber"),
            Reported("Raw Address", FormatRaw(node.RawDeviceAddress), "DEVPKEY_Device_Address"),
            Reported("BDF", node.Address?.ToString() ?? "未报告", "Validated / derived"),
            Field("Interrupt Support", node.Capabilities.InterruptSupport),
            Field("Interrupt Message Maximum", node.Capabilities.InterruptMessageMaximum),
            Field("BAR Types", node.Capabilities.BarTypes),
            Reported("Hardware IDs", Join(node.Identity.HardwareIds), "SetupAPI"),
            Reported("Compatible IDs", Join(node.Identity.CompatibleIds), "SetupAPI"),
            Reported("Location Paths", Join(node.Identity.LocationPaths), "SetupAPI"),
            Reported("Container ID", node.Identity.ContainerId?.ToString("D") ?? "未报告", "SetupAPI")
        ];
    }

    public static IReadOnlyList<PciDiagnosticFieldView> BuildDriver(PciTopologyNode node)
    {
        return
        [
            Reported("描述", Empty(node.Driver.Description), "Driver property"),
            Reported("提供方", Empty(node.Driver.Provider), "Driver property"),
            Reported("版本", Empty(node.Driver.Version), "Driver property"),
            Reported("INF", Empty(node.Driver.InfPath), "Driver property"),
            Reported("Service", Empty(node.Driver.Service), "Configuration Manager"),
            Reported("设备厂商", Empty(node.Identity.Manufacturer), "SetupAPI"),
            Reported("Enumerator", Empty(node.Identity.Enumerator), "Configuration Manager"),
            Reported("Class GUID", node.Identity.ClassGuid?.ToString("D") ?? "未报告", "SetupAPI"),
            Reported("DevNode Status", $"0x{node.Identity.Status.RawStatus:X8}", "Configuration Manager"),
            Reported("Problem Code", node.Identity.Status.ProblemCode?.ToString(CultureInfo.InvariantCulture) ?? "无", "Configuration Manager")
        ];
    }

    public static string BuildRawReport(PciTopologyNode node, PciTopologySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("PCI Express Node Diagnostic");
        builder.AppendLine($"Generated At       : {snapshot.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"Node ID            : {node.NodeId}");
        builder.AppendLine($"Parent Node ID     : {node.ParentNodeId ?? ""}");
        builder.AppendLine($"Child Node IDs     : {string.Join(", ", node.ChildNodeIds)}");
        builder.AppendLine($"Kind / Device Type : {node.Kind} / {node.DeviceType}");
        builder.AppendLine($"BDF                : {node.Address?.ToString() ?? ""}");
        builder.AppendLine($"Raw BusNumber      : {FormatRaw(node.RawBusNumber)}");
        builder.AppendLine($"Raw Address        : {FormatRaw(node.RawDeviceAddress)}");
        builder.AppendLine($"VID / DID          : {JoinIdentity(node.PciIdentity.VendorId, node.PciIdentity.DeviceId)}");
        builder.AppendLine($"Subsystem / Rev    : {Empty(node.PciIdentity.SubsystemId)} / {Empty(node.PciIdentity.RevisionId)}");
        builder.AppendLine($"Class              : {Empty(node.Class.Code)} / {node.Class.DisplayName}");
        builder.AppendLine();
        AppendFields(builder, "Link And Capabilities", BuildLinkAndCapabilities(node));
        AppendFields(builder, "Driver", BuildDriver(node));
        builder.AppendLine("Identity");
        builder.AppendLine($"  Instance ID      : {node.Identity.InstanceId}");
        builder.AppendLine($"  Hardware IDs     : {Join(node.Identity.HardwareIds)}");
        builder.AppendLine($"  Compatible IDs   : {Join(node.Identity.CompatibleIds)}");
        builder.AppendLine($"  Location Paths   : {Join(node.Identity.LocationPaths)}");
        builder.AppendLine($"  Container ID     : {node.Identity.ContainerId?.ToString("D") ?? ""}");
        builder.AppendLine();
        builder.AppendLine("Diagnostics");
        var diagnostics = snapshot.Diagnostics.Entries.Where(entry =>
            entry.NodeId is null || string.Equals(entry.NodeId, node.NodeId, StringComparison.OrdinalIgnoreCase));
        foreach (var diagnostic in diagnostics)
        {
            builder.AppendLine($"  {diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}");
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendFields(
        StringBuilder builder,
        string title,
        IEnumerable<PciDiagnosticFieldView> fields)
    {
        builder.AppendLine(title);
        foreach (var field in fields)
        {
            builder.AppendLine($"  {field.Label,-28}: {field.Value} [{field.Source}]");
            if (!string.IsNullOrWhiteSpace(field.Note))
            {
                builder.AppendLine($"    Note: {field.Note}");
            }
        }

        builder.AppendLine();
    }

    private static PciDiagnosticFieldView Field<T>(string label, TopologyFieldValue<T> field)
    {
        return new PciDiagnosticFieldView(
            label,
            field.DisplayText,
            field.Source.ToString(),
            field.Note ?? field.Availability.ToString());
    }

    private static PciDiagnosticFieldView Reported(string label, string value, string source)
    {
        return new PciDiagnosticFieldView(label, value, source);
    }

    private static string FormatRaw(uint? value)
    {
        return value.HasValue
            ? $"{value.Value.ToString(CultureInfo.InvariantCulture)} (0x{value.Value:X8})"
            : "未报告";
    }

    private static string JoinIdentity(string vendor, string device)
    {
        return string.IsNullOrWhiteSpace(vendor) && string.IsNullOrWhiteSpace(device)
            ? "未报告"
            : $"{Empty(vendor)} / {Empty(device)}";
    }

    private static string Join(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "未报告" : string.Join(Environment.NewLine, values);
    }

    private static string Empty(string value) => string.IsNullOrWhiteSpace(value) ? "未报告" : value;
}
