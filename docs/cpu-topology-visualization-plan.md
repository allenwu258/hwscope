# CPU Topology Visualization Plan

本文档定义 HwScope CPU topology 可视化的完整开发规划。目标不是把 `cpu-topology-inspector` 的命令行文本搬到 GUI 里，而是建立一个高内聚、低耦合、可扩展的 topology 绘制工具。CPU cache topology 是第一个落地场景，后续 PCIe topology、NUMA affinity、device topology 也应能复用同一套绘制、选择、高亮、缩放和导出能力。

参考输入：

```text
C:\Users\Trivedi\Downloads\cpu-topology-inspector.txt
```

当前基础：

- Stage 2 已接入 Windows `GetLogicalProcessorInformationEx`，能读取 groups、packages、NUMA nodes、cores、caches。
- Stage 2.1 已增加 `CpuTopologyInspectReport` 和 raw text inspect 窗口。
- CPU 页面已经有缓存摘要卡片和 Inspect 按钮。

## Implementation Status

当前已完成：

- Stage 2.2A：新增通用 `HwScope.App.Topology` 绘制骨架。
  - `TopologyDocument` / `TopologyNode` / `TopologyGroup` / `TopologyEdge` / `TopologyStyle`。
  - `NestedDomainLayoutEngine`。
  - `TopologyCanvas`，支持 document 渲染、zoom、selected item、highlighted item ids 和 item selected event。
  - 通用绘制层不引用 CPU / Core / Hardware 命名空间。
- Stage 2.2B：新增 `CpuTopologyVisualAdapter`。
  - `CpuTopologyInspectReport -> TopologyDocument`。
  - package group、L3 domain group、core nodes。
  - core node properties 包含 SMT、Efficiency、LP、L1D/L1I/L2/L3。
  - L3 badge 基于结构化 L3 cache size/domain count 推断，不依赖 raw text contains。
  - 多 package / multi-socket 使用 mask overlap 归属 L3 和 core。
- Stage 2.2C：Inspect 窗口已接入 Visual Map。
  - `Visual Map` / `Raw Report` tabs。
  - Visual Map 默认显示 drawn topology。
  - Raw Report 保留原始文本复制/保存。
  - zoom slider 使用 layout-aware scaling。
  - 右侧显示 Details / Legend / Notes。

尚未完成：

- Stage 2.2D：selection/highlight/details 抽象到通用 service/control。
- Stage 2.2E：PNG / JSON export。
- Tree / radial layouts。
- PCIe topology adapter。

## Goals

Topology 可视化要解决这些问题：

- 一眼看出 logical processor 属于哪个 physical core。
- 一眼看出 L1/L2/L3 cache 被哪些 cores / logical processors 共享。
- 对 AMD 多 L3 domain / CCD / V-Cache hint 给出直观可见的区域和 badge。
- 对 Intel hybrid core / efficiency class / E-core cluster 给出启发式可视提示。
- 保留 raw report 作为可复制、可验证的底层视图。
- 把绘制能力抽象成通用工具，后续 PCIe topo 不需要重写 Canvas、缩放、高亮、导出。

## Non-Goals

第一版不做：

- 不新建 WinUI 3 独立项目。
- 不用命令行文本模拟可视化。
- 不把 CCD / Intel cluster heuristic 当作硬件事实。
- 不在第一版同时完成 tree、radial、grid 三种布局。
- 不把 CPU-specific 判断写进通用绘图控件。

HwScope 当前 GUI 是 WPF + WPF-UI。Topology 可视化应继续在 `HwScope.App` 内实现，复用现有主题、窗口生命周期和 Fluent shell。

## Reference Plan Strengths

`cpu-topology-inspector.txt` 中有几个有价值的方向：

- 多布局意识正确：层次树形、径向同心圆、网格矩阵分别适合不同阅读方式。
- 交互目标正确：选择、高亮、缩放、平移、导出都适合 topology 工具。
- CCD 视觉编码有价值：AMD X3D 的 V-Cache / frequency CCD 应直接显示在 L3 domain 上。
- 导出目标合理：PNG 适合截图和报告，JSON 适合后续自动分析。
- 核心库提取思路正确：底层 topology 数据应从 UI 中分离。

需要调整的地方：

- 不采用 WinUI 3，避免和现有 WPF 应用形成双 UI 栈。
- 不创建 CPU-only GUI 项目，而是实现通用 topology drawing module。
- 不在第一版同时实现所有布局，先落地最稳定的 nested domain / grid map。
- 不让绘制层直接调用 Windows API 或做 CPU heuristic。

## Target Architecture

新增通用 topology 绘制模块：

```text
src/HwScope.App/Topology/
  Model/
    TopologyDocument.cs
    TopologyNode.cs
    TopologyEdge.cs
    TopologyGroup.cs
    TopologyStyle.cs
    TopologySelection.cs

  Layout/
    ITopologyLayoutEngine.cs
    TopologyLayoutOptions.cs
    TopologyLayoutResult.cs
    NestedDomainLayoutEngine.cs
    GridTopologyLayoutEngine.cs       future
    TreeTopologyLayoutEngine.cs       future
    RadialTopologyLayoutEngine.cs     future

  Controls/
    TopologyCanvas.xaml
    TopologyCanvas.xaml.cs
    TopologyNodeControl.xaml
    TopologyGroupControl.xaml
    TopologyDetailsPanel.xaml

  Interaction/
    TopologySelectionController.cs
    TopologyHighlightService.cs
    TopologyViewportState.cs

  Export/
    TopologyPngExporter.cs
    TopologyJsonExporter.cs
```

CPU-specific adapter：

```text
src/HwScope.App/Pages/Cpu/
  CpuTopologyVisualAdapter.cs
  CpuTopologyVisualViewModel.cs
```

Core 继续负责 CPU 事实与启发式：

```text
src/HwScope.Core/Hardware/Cpu/
  CpuTopologyInspectReport.cs
  CpuTopologyInspectFormatter.cs
  CpuTopologyVisualBuilder.cs       optional later
  CpuTopologyInsight.cs             already represented by insight records
```

分层原则：

- `HwScope.Core.Windows`：Windows API / unsafe parser。
- `HwScope.Core.Hardware.Cpu`：CPU topology facts and heuristic insights。
- `HwScope.App.Pages.Cpu`：CPU topology visual adapter。
- `HwScope.App.Topology`：domain-agnostic drawing tool。

## Common Topology Model

绘制层只认识通用 topology document。

```csharp
public sealed record TopologyDocument(
    string Id,
    string Title,
    IReadOnlyList<TopologyNode> Nodes,
    IReadOnlyList<TopologyGroup> Groups,
    IReadOnlyList<TopologyEdge> Edges,
    IReadOnlyList<TopologyLegendItem> Legend,
    IReadOnlyList<TopologyNote> Notes);
```

Node：

```csharp
public sealed record TopologyNode(
    string Id,
    string Kind,
    string Label,
    string? Subtitle,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<string> RelatedIds,
    TopologyStyle Style);
```

Group：

```csharp
public sealed record TopologyGroup(
    string Id,
    string Kind,
    string Label,
    string? ParentGroupId,
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<string> ChildGroupIds,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<TopologyBadge> Badges,
    TopologyStyle Style,
    bool IsHeuristic = false);
```

Edge：

```csharp
public sealed record TopologyEdge(
    string Id,
    string FromId,
    string ToId,
    string Kind,
    TopologyStyle Style);
```

Style：

```csharp
public sealed record TopologyStyle(
    string AccentKey,
    bool IsDashed = false,
    double Opacity = 1.0);
```

`AccentKey` 使用语义 token，而不是把颜色散落到业务逻辑：

```text
Cache.L1Data
Cache.L1Instruction
Cache.L2
Cache.L3
Cache.L3VCache
Core.Performance
Core.Efficiency
Group.Package
Group.Numa
Group.PcieRoot
Device.Pcie
Heuristic
```

## CPU Adapter

CPU adapter 把 `CpuTopologyInspectReport` 转换成 `TopologyDocument`。

映射关系：

```text
CPU package       -> TopologyGroup(kind=cpu.package)
NUMA node/group   -> TopologyGroup(kind=cpu.numa / cpu.processorGroup)
L3 cache domain   -> TopologyGroup(kind=cpu.l3Domain)
Physical core     -> TopologyNode(kind=cpu.core)
Logical processor -> small chip / child node / node property
L1/L2 cache       -> core tile property or child mini-node
```

CPU document 示例：

```text
Document: CPU Topology
  Group package-0
    Group l3-0
      Node core-0
      Node core-1
      ...
```

Core node properties：

```text
Core 00
SMT: yes
EfficiencyClass: 0
Logical processors: group 0 [0-1] mask=0x3
L1D: 32 KB, 8-way, line 64 B
L1I: 32 KB, 8-way, line 64 B
L2: 1 MB, 8-way, line 64 B
L3: 16 MB, 16-way, line 64 B
```

L3 group badge examples：

```text
single L3 domain
likely V-Cache CCD
likely frequency CCD
heuristic
```

Intel group badge examples：

```text
performance class
efficiency class
likely E-core cluster
heuristic
```

## First Layout: Nested Domain Map

第一版优先实现 nested domain / grid map，而不是 tree/radial。

原因：

- CPU cache sharing 天然是嵌套区域：Package -> L3 domain -> Core tile。
- 高信息密度，适合默认视图和截图。
- 不需要复杂边绘制，视觉噪音少。
- 对 AMD CCD 和 Intel L2 cluster 的表达最直接。
- 后续 PCIe topology 也可以复用 group/node/edge 基础能力。

默认视图：

```text
Package 0 · NUMA 0 · Group 0

L3 Unified · 16 MB · shared 16 LP · single L3 domain
┌────────────────────────────────────────────────────────┐
│ Core 00  SMT  Eff 0      Core 01  SMT  Eff 0           │
│ L1D 32K  L1I 32K        L1D 32K  L1I 32K               │
│ L2 1 MB                  L2 1 MB                       │
│ LP0 LP1                  LP2 LP3                       │
└────────────────────────────────────────────────────────┘
```

AMD multi-CCD example：

```text
L3 group 0 · 96 MB · likely V-Cache CCD · heuristic
  Core 00 ... Core 07

L3 group 1 · 32 MB · likely frequency CCD · heuristic
  Core 08 ... Core 15
```

Intel hybrid example：

```text
Performance class
  P-core tiles, SMT=true

Efficiency class
  E-core cluster group, shared L2 domain
```

## Layout Engine

通用接口：

```csharp
public interface ITopologyLayoutEngine
{
    TopologyLayoutResult Layout(TopologyDocument document, TopologyLayoutOptions options);
}
```

Options：

```csharp
public sealed record TopologyLayoutOptions(
    double AvailableWidth,
    TopologyDensity Density,
    bool ShowL1Caches,
    bool ShowL2Caches,
    bool ShowLogicalProcessors);
```

Result：

```csharp
public sealed record TopologyLayoutResult(
    Size CanvasSize,
    IReadOnlyDictionary<string, Rect> GroupBounds,
    IReadOnlyDictionary<string, Rect> NodeBounds,
    IReadOnlyDictionary<string, Point> EdgePorts);
```

`NestedDomainLayoutEngine` 规则：

- group 可以嵌套。
- group 内 node 自动 wrap。
- node size 由 density 决定。
- group size 由 child bounds + padding 决定。
- 输出矩形 bounds，Canvas 只负责渲染。

后续 layout engines：

- `TreeTopologyLayoutEngine`：Package -> L3 -> L2 -> L1 -> LP。
- `RadialTopologyLayoutEngine`：Package center -> L3 ring -> Core ring -> LP ring。
- `PcieLaneLayoutEngine`：Root complex -> switch -> endpoint，强调 edge/link。

## TopologyCanvas Control

通用控件：

```text
TopologyCanvas
  Document
  LayoutMode
  Density
  Zoom
  SelectedItemId
  HighlightedItemIds
  ItemSelected event
```

职责：

- 渲染 groups。
- 渲染 nodes。
- 渲染 edges。
- hover / selected / highlighted 状态。
- zoom。
- tooltip。
- expose visual for PNG export。

不负责：

- 不调用 Windows API。
- 不判断 CCD。
- 不判断 Intel P/E-core。
- 不知道 CPU/PCIe 的业务语义。

## Interaction Design

第一版必须支持：

- 点击 L3 group：高亮包含的 cores / LP。
- 点击 core tile：高亮它的 L1/L2/L3/LP。
- 点击 LP chip：高亮所属 core 和 cache domains。
- 右侧详情面板显示 selected item properties。
- Tooltip 显示 mask、cache size、line size、associativity。
- Zoom slider。
- Fit to width。
- Raw Report tab 保留现有文本。

第二版增强：

- pan drag。
- search by core / LP / group / mask。
- minimap。
- keyboard navigation。
- collapse L1/L2 details。

## Window UX

现有 `CpuTopologyInspectWindow` 可升级为：

```text
CPU Topology Inspect
  Header
    CPU name
    generated time
    [Copy Raw] [Save Raw] [Export PNG] [Export JSON]

  Tabs
    Visual Map
      Toolbar
        Layout: Domain Map
        Density: Compact / Detailed
        Zoom: 50%-200%
        Show: L1 / L2 / LP / Hints
      Main
        TopologyCanvas
      Right
        DetailsPanel
        Legend
        Notes

    Raw Report
      readonly monospace text
```

Default tab should be `Visual Map`; raw text remains available for copy/debug.

## Visual Language

Use existing HwScope theme tokens for surfaces/text/borders. Accent colors should be semantic and limited.

Recommended accent mapping:

```text
L1 Data         #3498DB
L1 Instruction  #9B59B6
L2              #2ECC71
L3              #E67E22
L3 V-Cache      #E74C3C
P-core          #3498DB
E-core          #95A5A6
Heuristic       dashed border / small badge
```

Important visual rules:

- Heuristic groups use dashed border or badge, not only color.
- Raw masks should be visible in detail panel, not always on every tile.
- Core tile text must stay compact and non-overlapping.
- High-core CPUs should degrade to compact mode automatically.
- Do not use decorative gradients/orbs; this is a diagnostic tool.

## Export

Stage 2.2 first pass:

- Keep raw text copy/save from Stage 2.1.

Stage 2.2 later:

- Export current visual as PNG via WPF `RenderTargetBitmap`.
- Export `TopologyDocument` as JSON.

Export boundaries:

- PNG export belongs to `HwScope.App.Topology.Export`.
- JSON export should serialize the generic `TopologyDocument`, not CPU-only records.
- Raw CPU inspect text remains separate.

## PCIe Reuse Path

PCIe topology should be able to reuse:

- `TopologyDocument`
- `TopologyCanvas`
- layout engine infrastructure
- selection/highlight service
- details panel
- PNG/JSON export

PCIe adapter later maps:

```text
Root Complex     -> TopologyGroup(kind=pcie.rootComplex)
PCIe Switch      -> TopologyGroup(kind=pcie.switch)
Endpoint Device  -> TopologyNode(kind=pcie.device)
PCIe Link        -> TopologyEdge(kind=pcie.link)
NUMA Affinity    -> group / node property
Lane Width/Speed -> edge properties
```

This is why CPU-specific logic must stay out of `TopologyCanvas`.

## Development Plan

### Stage 2.2A: Generic Topology Drawing Skeleton - Done

Add:

```text
src/HwScope.App/Topology/Model/*
src/HwScope.App/Topology/Layout/NestedDomainLayoutEngine.cs
src/HwScope.App/Topology/Controls/TopologyCanvas.xaml(.cs)
```

Acceptance:

- Mock `TopologyDocument` renders groups and nodes.
- Zoom works.
- Item selection event works.
- No CPU-specific references inside topology controls.
- `dotnet build` passes.

### Stage 2.2B: CPU Visual Adapter - Done

Add:

```text
src/HwScope.App/Pages/Cpu/CpuTopologyVisualAdapter.cs
```

Responsibilities:

- Convert `CpuTopologyInspectReport` to `TopologyDocument`.
- Build L3 domain groups.
- Build core nodes.
- Attach LP/cache properties.
- Attach AMD/Intel badges and heuristic notes.

Acceptance:

- Current Ryzen machine shows one L3 domain containing 8 core tiles.
- Each core tile shows core index, SMT, efficiency class, LP chips.
- Detail panel can show L1/L2/L3 data for selected core.

### Stage 2.2C: Integrate Visual Map Into Inspect Window - Done

Modify:

```text
src/HwScope.App/Windows/CpuTopologyInspectWindow.xaml(.cs)
```

Acceptance:

- Inspect window has `Visual Map` and `Raw Report` tabs.
- Visual Map is default.
- Raw Report keeps existing text behavior.
- Copy/save raw report still works.

Current notes:

- Basic selection, recursive group highlight and details rendering are implemented in window code-behind.
- Stage 2.2D should move this logic into reusable topology selection/highlight/details components.

### Stage 2.2D: Selection And Highlight - Planned

Add:

```text
TopologySelectionController
TopologyHighlightService
TopologyDetailsPanel
```

Acceptance:

- Selecting L3 highlights contained cores/LP.
- Selecting core highlights its cache domains/LP.
- Selecting LP highlights parent core and cache domains.
- Right panel updates selected item details.

### Stage 2.2E: Export - Planned

Add:

```text
TopologyPngExporter
TopologyJsonExporter
```

Acceptance:

- Export PNG creates a readable topology image.
- Export JSON emits generic `TopologyDocument`.
- Export does not depend on CPU-only model.

### Stage 2.3: Additional Layouts

Add:

- Tree layout.
- Radial layout.
- Layout switcher.
- Per-layout validation on single-L3, multi-L3, and high-core mock data.

### Stage 2.4: PCIe Topology Reuse

Add PCIe adapter only after generic drawing layer is stable.

Acceptance:

- PCIe adapter can render using the same `TopologyCanvas`.
- No changes required in CPU adapter for PCIe support.

## Testing Plan

Build:

```powershell
dotnet build
```

Manual validation:

- Open CPU page.
- Open cache Inspect.
- Confirm Visual Map appears.
- Switch to Raw Report.
- Click L3 group/core/LP and verify highlight/details.
- Toggle compact/detailed mode.
- Verify light/dark theme readability.
- Resize window and verify no text overlap.

Data validation:

- Current 8C/16T Ryzen:
  - 1 package.
  - 1 NUMA node.
  - 1 L3 domain.
  - 8 core tiles.
  - 16 LP chips.
- Mock AMD X3D:
  - 2 L3 domains.
  - larger L3 marked likely V-Cache.
  - smaller L3 marked likely frequency CCD.
- Mock Intel hybrid:
  - multiple efficiency classes.
  - SMT P-core-like group.
  - L2 shared E-core cluster hint.

Future tests:

- `NestedDomainLayoutEngine` layout bounds.
- `CpuTopologyVisualAdapter` conversion.
- `TopologyHighlightService` related-id calculation.
- JSON export shape.

## Risks And Mitigations

### UI Complexity

Risk: visualization grows into CPU-specific custom UI.

Mitigation:

- Enforce `TopologyDocument` boundary.
- Keep CPU adapter outside `HwScope.App.Topology`.
- Review namespace dependencies.

### High-Core Performance

Risk: 64+ core systems render too many tiles.

Mitigation:

- Compact density default for high core counts.
- Collapse L1/L2 details.
- Consider virtualization later.

### Heuristic Misinterpretation

Risk: Users treat CCD / hybrid labels as factual.

Mitigation:

- Use `heuristic` badge.
- Use dashed border.
- Include validation note.
- Keep raw mask/report visible.

### Layout Overreach

Risk: Tree/radial/grid all-at-once delays usable output.

Mitigation:

- Ship nested domain map first.
- Add layout engines only after generic model/control is stable.

## Definition Of Done For Stage 2.2

Stage 2.2 is complete when:

- A reusable topology drawing module exists under `HwScope.App.Topology`.
- CPU inspect window shows a drawn Visual Map, not command-line text.
- Raw Report remains available.
- L3 domain/core/LP selection and highlight work.
- Details panel displays selected item properties.
- AMD/Intel heuristic badges are visible and clearly marked.
- Drawing layer has no CPU-specific dependency.
- `dotnet build` passes.
