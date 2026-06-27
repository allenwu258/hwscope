using HwScope.App.Topology.Model;

namespace HwScope.App.Topology.Layout;

public interface ITopologyLayoutEngine
{
    TopologyLayoutResult Layout(TopologyDocument document, TopologyLayoutOptions options);
}
