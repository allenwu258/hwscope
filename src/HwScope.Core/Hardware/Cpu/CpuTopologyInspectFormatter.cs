using System.Text;

namespace HwScope.Core.Hardware.Cpu;

public static class CpuTopologyInspectFormatter
{
    public static string Format(CpuTopologyInspectReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("CPU Topology Inspector");
        builder.AppendLine("======================");
        builder.AppendLine();

        AppendGroups(builder, report);
        AppendPackages(builder, report);
        AppendNumaNodes(builder, report);
        AppendCores(builder, report);
        AppendCaches(builder, report);
        AppendInsights(builder, report, "Likely CCD / L3 Groups", insight => insight.Title.StartsWith("L3 group", StringComparison.OrdinalIgnoreCase));
        AppendInsights(builder, report, "Hybrid / Cluster Hints", insight => insight.Title.StartsWith("Hybrid", StringComparison.OrdinalIgnoreCase) || insight.Title.StartsWith("L2 cluster", StringComparison.OrdinalIgnoreCase));

        if (report.Notes.Count > 0)
        {
            builder.AppendLine("Notes");
            builder.AppendLine("-----");
            foreach (var note in report.Notes)
            {
                builder.AppendLine(note);
            }
            builder.AppendLine();
        }

        builder.Append("Generated at: ");
        builder.AppendLine(report.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss"));
        return builder.ToString().TrimEnd();
    }

    private static void AppendGroups(StringBuilder builder, CpuTopologyInspectReport report)
    {
        builder.AppendLine("Processor Groups");
        builder.AppendLine("----------------");
        if (report.Groups.Count == 0)
        {
            builder.AppendLine("No group information returned.");
        }

        var maximumGroups = report.Groups.Count == 0 ? 0 : report.Groups.Max(group => group.Group + 1);
        if (report.Groups.Count > 0)
        {
            builder.AppendLine($"Maximum groups: {maximumGroups}, active groups: {report.Groups.Count}");
        }

        foreach (var group in report.Groups)
        {
            builder.AppendLine($"  Group {group.Group}: active {group.ActiveProcessorCount}/{group.MaximumProcessorCount}, logical processors: {group.ActiveMask.DisplayText}");
        }

        builder.AppendLine();
    }

    private static void AppendPackages(StringBuilder builder, CpuTopologyInspectReport report)
    {
        builder.AppendLine("Packages / Sockets");
        builder.AppendLine("------------------");
        if (report.Packages.Count == 0)
        {
            builder.AppendLine("No package information returned.");
        }

        foreach (var package in report.Packages)
        {
            builder.AppendLine($"Package {package.PackageIndex}: {FormatMasks(package.LogicalProcessors)}");
        }

        builder.AppendLine();
    }

    private static void AppendNumaNodes(StringBuilder builder, CpuTopologyInspectReport report)
    {
        builder.AppendLine("NUMA Nodes");
        builder.AppendLine("----------");
        if (report.NumaNodes.Count == 0)
        {
            builder.AppendLine("No NUMA node information returned.");
        }

        foreach (var node in report.NumaNodes)
        {
            builder.AppendLine($"NUMA node {node.NodeNumber}: {node.LogicalProcessors.DisplayText}");
        }

        builder.AppendLine();
    }

    private static void AppendCores(StringBuilder builder, CpuTopologyInspectReport report)
    {
        builder.AppendLine("Physical Cores and Logical Processor Mapping");
        builder.AppendLine("---------------------------------------------");
        if (report.Cores.Count == 0)
        {
            builder.AppendLine("No core information returned.");
        }

        foreach (var core in report.Cores)
        {
            builder.AppendLine($"Core {core.CoreIndex:D2}: SMT={(core.HasSmt ? "yes" : "no")}, efficiencyClass={core.EfficiencyClass}, logical processors: {FormatMasks(core.LogicalProcessors)}");
        }

        builder.AppendLine();
    }

    private static void AppendCaches(StringBuilder builder, CpuTopologyInspectReport report)
    {
        builder.AppendLine("Cache Topology");
        builder.AppendLine("--------------");
        if (report.CacheInstances.Count == 0)
        {
            builder.AppendLine("No cache information returned.");
        }

        foreach (var cache in report.CacheInstances.OrderBy(cache => cache.Level).ThenBy(cache => cache.CacheIndex))
        {
            builder.AppendLine($"L{cache.Level} {cache.CacheType,-11} {FormatBytes(cache.SizeBytes),10}  line={cache.LineSizeBytes,4}  assoc={FormatAssociativity(cache.Associativity),4}  logical processors: {cache.LogicalProcessors.DisplayText}");
        }

        builder.AppendLine();
    }

    private static void AppendInsights(StringBuilder builder, CpuTopologyInspectReport report, string title, Func<CpuTopologyInsight, bool> predicate)
    {
        var insights = report.Insights.Where(predicate).ToList();
        if (insights.Count == 0)
        {
            return;
        }

        builder.AppendLine(title);
        builder.AppendLine(new string('-', title.Length));
        foreach (var insight in insights)
        {
            builder.AppendLine($"{insight.Title}: {insight.Detail}");
        }

        builder.AppendLine();
    }

    private static string FormatMasks(IEnumerable<CpuProcessorMaskView> masks)
    {
        return string.Join("; ", masks.Select(mask => mask.DisplayText));
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
        {
            return $"{bytes / 1024L / 1024L / 1024L} GB";
        }

        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / 1024L / 1024L} MB";
        }

        if (bytes >= 1024L)
        {
            return $"{bytes / 1024L} KB";
        }

        return $"{bytes} B";
    }

    private static string FormatAssociativity(int associativity)
    {
        return associativity == 0xff ? "full" : associativity.ToString();
    }
}
