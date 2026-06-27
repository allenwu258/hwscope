# CPU Stage 2 Topology And Cache Status

本文档记录 HwScope CPU 模块 Stage 2 的设计、实现状态和后续边界。Stage 2 已把 Stage 1 的 `WMI + 本地映射` CPU 页面升级为 `Windows 真实拓扑 + 真实缓存共享` 页面，优先接入 `GetLogicalProcessorInformationEx`，而不是继续扩大 `CpuKnownProcessorCatalog`。

参考项目：

```text
C:\Users\Trivedi\projects\cpu-topology-inspector
```

该项目是一个 .NET 8 console，核心逻辑在 `Program.cs`，通过 Windows `GetLogicalProcessorInformationEx(RelationAll)` 一次性读取 processor groups、packages、physical cores、cache topology 和 group masks。

## Implementation Status

Stage 2 当前已完成：

- 新增 `HwScope.Core.Windows.LogicalProcessorInformation`，通过 `GetLogicalProcessorInformationEx(RelationAll)` 读取 Windows 逻辑处理器拓扑。
- 新增 `HwScope.Core.Hardware.Cpu.CpuTopologyAnalyzer`，把 Windows 原始拓扑转换为 CPU 详情页领域模型。
- `CpuDetailCollector` 已优先使用 Windows API 提供 package/core/logical processor、SMT、CPU group、NUMA node 和 cache 数据。
- `CpuCacheInfo` 已扩展 `CacheType` 和 `SharedMasks`。
- `CpuDetailReport` 已扩展 `CoreMappings`。
- CPU 页面已增加 `核心映射` section。
- 文本报告导出已包含核心映射，并在缓存行展示 line size 和 shared logical processor count。
- `dotnet build` 已验证通过。

Stage 2 暂未实现：

- L3 / CCD 洞察 section。
- native CPUID worker。
- 真实 instruction set feature flags。
- 传感器、电压、温度、功耗。
- UI 上针对超多核心机器的折叠/虚拟化列表优化。

## Goals

Stage 2 已补齐这些原本缺失或只靠映射的数据：

- 物理 package 数。
- 物理核心数。
- 逻辑线程数。
- NUMA node 数。
- CPU group 数。
- 每个物理核心对应哪些 logical processors。
- SMT 是否开启。
- efficiencyClass，后续用于 Intel P-core / E-core 区分。
- L1/L2/L3 cache size。
- cache type: Data / Instruction / Unified / Trace。
- cache line size。
- associativity。
- cache shared logical processor count。
- cache 被哪些 logical processors 共享。

## Non-Goals

Stage 2 不做，后续阶段再接入：

- 不做 native CPUID worker。
- 不做真实 instruction set feature flags。
- 不做传感器、电压、温度、功耗。
- 不做线程亲和性控制。
- 不把 CCD 推断当作事实。

CPUID worker 仍应作为后续阶段。Windows topology API 给的是 OS topology，不等于完整 CPUID。

## Reference Project Findings

`cpu-topology-inspector` 的关键流程：

1. 调用 `GetLogicalProcessorInformationEx(RelationAll, null, ref length)` 获取 buffer 长度。
2. 分配 byte buffer。
3. 再次调用 `GetLogicalProcessorInformationEx` 填充 buffer。
4. 按 `SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX` 记录逐条解析。
5. 根据 `Relationship` 分派到：
   - `RelationProcessorCore`
   - `RelationCache`
   - `RelationProcessorPackage`
   - `RelationGroup`
6. 把 `GROUP_AFFINITY` 保存为 `(Group, Mask)`，而不是压成单个逻辑处理器列表。

它最有价值的点：

- 用 Windows 拓扑 API 替代硬编码 cache。
- 把 cache sharing 作为 CPU 结构核心信息。
- 通过 L3 group 数量和容量做 CCD / V-Cache 方向的启发式诊断。

## Target Architecture

Stage 2 后的结构：

```text
HwScope.Core
  Windows/
    LogicalProcessorInformation.cs
      P/Invoke GetLogicalProcessorInformationEx
      unsafe buffer parser
      Windows-only topology records

  Hardware/Cpu/
    CpuDetailCollector.cs
      merges WMI + topology API + mapping fallback

    CpuTopologyAnalyzer.cs
      converts raw Windows topology to CpuDetailReport-friendly data

    CpuDetailReport.cs
      extended with core mapping and cache sharing fields

HwScope.App
  Pages/CpuDetailPage.xaml(.cs)
    cache section uses API data
    new core mapping section
    optional topology insights section
```

P/Invoke and unsafe offset parsing must stay inside `HwScope.Core.Windows`. The CPU business layer should consume typed records only.

## Core Windows API Layer

### File

```text
src/HwScope.Core/Windows/LogicalProcessorInformation.cs
```

### Implemented Shape

Keep it internal to Core first:

```csharp
internal static class LogicalProcessorInformation
{
    public static LogicalProcessorTopology? TryCollect();
}
```

Return shape:

```csharp
internal sealed record LogicalProcessorTopology(
    int PackageCount,
    int PhysicalCoreCount,
    int LogicalProcessorCount,
    int ActiveGroupCount,
    int MaximumGroupCount,
    int NumaNodeCount,
    IReadOnlyList<LogicalProcessorGroup> Groups,
    IReadOnlyList<LogicalCoreInfo> Cores,
    IReadOnlyList<LogicalCacheInfo> Caches,
    IReadOnlyList<LogicalPackageInfo> Packages);
```

当前实现还保留了 `IReadOnlyList<LogicalNumaNodeInfo> NumaNodes`，用于 NUMA node 计数和后续拓扑展示扩展。

Group mask:

```csharp
internal sealed record LogicalProcessorMask(
    ushort Group,
    ulong Mask)
{
    public IReadOnlyList<int> LocalProcessorIndexes { get; }
    public int Count { get; }
    public string DisplayText { get; }
}
```

Core:

```csharp
internal sealed record LogicalCoreInfo(
    int Index,
    bool HasSmt,
    byte EfficiencyClass,
    IReadOnlyList<LogicalProcessorMask> Masks);
```

Cache:

```csharp
internal sealed record LogicalCacheInfo(
    byte Level,
    LogicalCacheType Type,
    long SizeBytes,
    int LineSizeBytes,
    int Associativity,
    LogicalProcessorMask Mask);
```

Package:

```csharp
internal sealed record LogicalPackageInfo(
    int Index,
    IReadOnlyList<LogicalProcessorMask> Masks);
```

Group:

```csharp
internal sealed record LogicalProcessorGroup(
    ushort Group,
    int MaximumProcessorCount,
    int ActiveProcessorCount,
    LogicalProcessorMask ActiveMask);
```

Cache type enum:

```csharp
internal enum LogicalCacheType
{
    Unified,
    Instruction,
    Data,
    Trace,
    Unknown
}
```

### Supported Relationships

Must parse:

- `RelationProcessorCore = 0`
- `RelationNumaNode = 1`, optional but recommended for Stage 2.
- `RelationCache = 2`
- `RelationProcessorPackage = 3`
- `RelationGroup = 4`

Can ignore in Stage 2:

- `RelationProcessorDie`
- `RelationNumaNodeEx`
- `RelationProcessorModule`
- future Windows relationships

Unknown relationships should be skipped, not fatal.

### Parsing Notes

The reference project uses unsafe fixed offsets. Stage 2 can reuse the concept, but must isolate it.

Important offsets from the reference:

- record header:
  - `Relationship`: offset 0, `int`
  - `Size`: offset 4, `uint`
  - body: offset 8
- core body:
  - flags: offset 0
  - efficiencyClass: offset 1
  - groupCount: offset 22
  - group masks: offset 24, stride 16
- cache body:
  - level: offset 0
  - associativity: offset 1
  - lineSize: offset 2
  - cacheSize: offset 4
  - type: offset 8
  - group mask: offset 32
- package body:
  - groupCount: offset 22
  - group masks: offset 24, stride 16
- group body:
  - maximumGroupCount: offset 0
  - activeGroupCount: offset 2
  - group info array: offset 24, stride 48

Implementation should validate:

- `Size > 0`
- cursor never advances past end
- group count does not cause reads past record boundary
- `nuint` is cast to `ulong` for display/mask iteration

## CpuDetailReport Changes

Extend existing CPU model without breaking Stage 1 callers.

### CpuCacheInfo

Stage 1 shape:

```csharp
public sealed record CpuCacheInfo(
    CpuCacheLevel Level,
    string Name,
    int? InstanceCount,
    long? SizeBytes,
    int? Ways,
    int? LineSizeBytes,
    int? SharedLogicalProcessorCount,
    CpuDataSource Source,
    bool IsEstimated = false,
    string? Note = null);
```

Stage 2 已添加：

```csharp
string? CacheType
string? SharedLogicalProcessors
```

并引入：

```csharp
public sealed record CpuProcessorMaskView(
    ushort Group,
    string ProcessorRange,
    string HexMask,
    int Count);
```

实际实现：

```csharp
IReadOnlyList<CpuProcessorMaskView> SharedMasks
```

### New Core Mapping

Add:

```csharp
public sealed record CpuCoreMappingInfo(
    int CoreIndex,
    bool HasSmt,
    int EfficiencyClass,
    IReadOnlyList<CpuProcessorMaskView> LogicalProcessors,
    CpuDataSource Source);
```

Extend `CpuDetailReport`:

```csharp
IReadOnlyList<CpuCoreMappingInfo> CoreMappings
```

This allows the UI to show:

```text
Core 00: SMT yes, efficiency 0, group 0 [0-1]
Core 01: SMT yes, efficiency 0, group 0 [2-3]
```

### Topology Fields

Replace placeholders when topology API succeeds:

- `PackageCount`: Windows API.
- `CoreCount`: Windows API.
- `LogicalProcessorCount`: Windows API.
- `SmtEnabled`: computed from core mappings.
- `CpuGroupCount`: Windows API.
- `NumaNodeCount`: Windows API.

If API fails, keep current WMI fallback.

## CpuTopologyAnalyzer

### File

```text
src/HwScope.Core/Hardware/Cpu/CpuTopologyAnalyzer.cs
```

Purpose: convert `LogicalProcessorTopology` into CPU domain objects.

Responsibilities:

- Aggregate cache instances by level/type/size/ways.
- Convert cache masks to display-ready `CpuProcessorMaskView`.
- Convert core records to `CpuCoreMappingInfo`.
- Compute physical core count and SMT status.
- Generate topology notes.
- Generate optional L3 group insights.

### Cache Aggregation

Windows returns one cache record per shared cache scope. For current Ryzen:

```text
8 x L1 Data 32 KB
8 x L1 Instruction 32 KB
8 x L2 Unified 1 MB
1 x L3 Unified 16 MB
```

HwScope cache summary should group identical caches:

```text
L1 Data          8 x 32 KB    8-way    line 64 B    shared by 2 LP each
L1 Instruction   8 x 32 KB    8-way    line 64 B    shared by 2 LP each
L2 Unified       8 x 1 MB     8-way    line 64 B    shared by 2 LP each
L3 Unified       16 MB        16-way   line 64 B    shared by 16 LP
```

Grouping key:

```text
level + type + sizeBytes + associativity + lineSizeBytes + sharedLogicalProcessorCount
```

For detailed view, keep individual cache records available.

## CpuDetailCollector Merge Flow

New flow:

```text
Collect()
  WMI data
  known processor mapping
  topology = TryCollectLogicalProcessorTopology()

  identity/specification:
    WMI + mapping

  topology section:
    topology API if available
    else WMI

  cache section:
    topology API if available
    else mapping if available
    else placeholders

  core mappings:
    topology API if available
    else empty

  notes:
    API source note
    fallback note only when needed
```

Missing topology API should not throw through to UI. It should add a note and continue with WMI/mapping.

## UI Changes

### Cache Section

Stage 2 should display real API cache rows:

```text
L1 Data          8 x 32 KB    8-way    line 64 B    shared 2 LP
L1 Instruction   8 x 32 KB    8-way    line 64 B    shared 2 LP
L2 Unified       8 x 1 MB     8-way    line 64 B    shared 2 LP
L3 Unified       16 MB        16-way   line 64 B    shared 16 LP
```

Existing `CpuDetailReportFormatter.FormatCache` should be enhanced to include:

- cache type
- line size
- shared logical processor count

### Core Mapping Section

Add a collapsible or compact section:

```text
核心映射
Core 00    SMT yes    Eff 0    group 0 [0-1]    mask=0x3
Core 01    SMT yes    Eff 0    group 0 [2-3]    mask=0xC
...
```

当前实现采用普通 section，位置在 `拓扑` 后面。后续如果高核心数机器上列表过长，再在 Stage 2.1 做折叠、搜索或虚拟化。

### Topology Insight Section

Optional later in Stage 2:

```text
L3 / CCD 洞察
L3 group 0: 16 MB, shared by group 0 [0-15]
```

For X3D / dual CCD:

```text
L3 group 0: 96 MB, likely V-Cache CCD
L3 group 1: 32 MB, likely frequency CCD
```

Important: label as inferred/heuristic.

## Formatter Changes

Text export should include:

```text
核心映射：
  Core 00：SMT 是，Efficiency 0，group 0 [0-1] mask=0x3

缓存：
  L1 Data：8 x 32 KB，8-way，line 64 B，shared 2 logical processors
```

Notes should say:

```text
拓扑和缓存来自 Windows GetLogicalProcessorInformationEx。
CCD 提示基于 L3 cache 分组和容量推断，仅供参考。
```

## Milestones

### Milestone 1: Port Topology Reader - Done

Add `LogicalProcessorInformation.cs`.

Acceptance:

- `dotnet build` passes.
- A temporary smoke call can collect topology on current machine.
- No UI changes yet.

### Milestone 2: Merge Topology Into CpuDetailCollector - Done

Use topology API as preferred source for:

- package count
- physical core count
- logical processor count
- SMT enabled
- CPU group count
- NUMA node count
- cache rows

Acceptance:

- CPU page no longer shows `CPU Groups` and `NUMA Nodes` as placeholders on supported Windows.
- Cache source badge becomes `API`.
- Cache rows show line size and shared logical processor count.

### Milestone 3: Core Mapping UI - Done

Add core mapping section.

Acceptance:

- CPU page shows core-to-logical-processor mapping.
- Current machine should show 8 cores, each with 2 logical processors.
- Long lists remain scrollable and do not break layout.

### Milestone 4: L3 / CCD Insight - Planned

Add optional L3 grouping summary.

Acceptance:

- Single L3 CPU shows simple L3 sharing summary.
- Multi-L3 CPU shows each L3 group separately.
- V-Cache/frequency CCD hints are marked as heuristic.

## Validation On Current Machine

Expected based on `cpu-topology-inspector` output:

```text
Groups: 1 active group
Package: 1
Cores: 8
Logical processors: 16
SMT: yes
L1D: 8 x 32 KB, 8-way, line 64 B, shared by 2 LP
L1I: 8 x 32 KB, 8-way, line 64 B, shared by 2 LP
L2: 8 x 1 MB, 8-way, line 64 B, shared by 2 LP
L3: 16 MB, 16-way, line 64 B, shared by 16 LP
```

Core mapping:

```text
Core 00 -> group 0 [0-1]
Core 01 -> group 0 [2-3]
...
Core 07 -> group 0 [14-15]
```

## Testing Plan

Current repo has no test project. Stage 2 当前以 build + smoke/manual validation 为主。如果添加测试：

```text
src/HwScope.Core.Tests/
```

Useful tests:

- `LogicalProcessorMask` range compression.
- cache grouping logic.
- associativity formatting, including `0xff` full associativity.
- topology fallback when API throws/fails.
- formatter output for API cache rows.

Manual validation:

```powershell
dotnet build
dotnet run --project .\src\HwScope.App
```

Then inspect CPU page:

- Topology fields show API values.
- Cache rows show line/shared fields.
- Core mapping section appears.
- Copy/export includes topology details.
- Light/dark theme remains readable.

## Risks And Mitigations

### Unsafe Offset Parsing

Risk: wrong offsets can crash or produce invalid topology.

Mitigation:

- Keep all unsafe parsing in `HwScope.Core.Windows`.
- Validate record size before reading variable arrays.
- Skip unknown relationships.
- Fail closed and fall back to WMI.

### Multi-Group CPUs

Risk: flattening processors to one int list breaks CPUs with more than 64 logical processors.

Mitigation:

- Keep `(Group, Mask)` throughout the model.
- Display ranges per group.
- Do not store global logical processor IDs until there is a well-defined mapping.

### CCD Inference

Risk: L3 size heuristic can be wrong.

Mitigation:

- Keep CCD labels as insight, not core data.
- Use words like `likely` / `推测`.
- Mention validation with Ryzen Master/HWiNFO.

### OS Topology Versus CPUID

Risk: Windows topology does not expose all CPUID details.

Mitigation:

- Keep Stage 3 native CPUID worker in the roadmap.
- Do not remove CPUID placeholders for family/model/features until native data exists.

### UI Scale

Risk: core mapping can be very long on high-core systems.

Mitigation:

- Put mapping in scrollable/collapsible section.
- Show summary first, details below.

## Recommended Next Steps

1. Add focused tests for mask range compression and cache grouping.
2. Build a drawn topology visualization tool for cache/core/LP sharing. See `docs/cpu-topology-visualization-plan.md`.
3. Add L3 sharing summary and optional CCD/V-Cache heuristic insight, clearly marked as inferred.
4. Improve core mapping UI for high-core systems with collapse/search/virtualization.
5. Start the native CPUID stage for raw family/model/stepping and real feature flags.
6. Keep Windows API topology as the primary OS topology source even after CPUID lands.
