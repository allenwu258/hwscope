namespace HwScope.App.Topology.Layout;

public sealed record TopologyLayoutOptions(
    double AvailableWidth,
    TopologyDensity Density,
    bool ShowL1Caches,
    bool ShowL2Caches,
    bool ShowLogicalProcessors)
{
    public static TopologyLayoutOptions Default { get; } = new(
        AvailableWidth: 900,
        Density: TopologyDensity.Detailed,
        ShowL1Caches: true,
        ShowL2Caches: true,
        ShowLogicalProcessors: true);
}

public enum TopologyDensity
{
    Compact,
    Detailed
}

public enum TopologyLayoutMode
{
    NestedDomains,
    Hierarchical
}
