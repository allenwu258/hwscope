# CPU Detail Page Requirements And Design

本文档定义 HwScope CPU 详细内容页面的需求分析和页面设计。参考对象是 CPU-Z 的 `处理器` 页和 AIDA64 的 `CPUID` 窗口，但目标不是复刻视觉皮肤，而是吸收它们的信息密度、字段组织和诊断逻辑，做成符合 HwScope Fluent/WPF shell 的本地硬件详情页。

## Goals

CPU 详情页要解决三个问题：

- 让用户一眼确认 CPU 身份、平台、核心线程、缓存、指令集和当前频率。
- 给进阶用户提供接近 CPU-Z / AIDA64 CPUID 的可验证字段，方便截图、排查和对比。
- 为后续跑分、压力测试、传感器和导出报告提供结构化 CPU 数据基础。

当前硬件摘要只返回展示字符串，例如 `AMD Ryzen 7 8745H w/ Radeon 780M Graphics @ 3.80GHz 8核16线程`。CPU 详情页需要从字符串摘要升级为结构化模型。

## Non-Goals

第一版不做以下内容：

- 不实现完整 CPU-Z 所有 tab，例如主板、内存、SPD、显卡、基准跑分。
- 不做低层驱动级传感器读取，例如精确 VID、每核心电压、功耗、温度。
- 不保证所有字段都能在所有机器上采集成功。采集不到时要显示明确的空状态，而不是伪造值。
- 不把 CPU 详情页做成独立皮肤窗口。它应优先作为主窗口导航里的硬件详情页。

## Reference Analysis

### CPU-Z Processor Page

CPU-Z 的优点是极高密度和明确分区：

- 顶部身份区：名字、代号、TDP、插槽、工艺、电压、规格、系列/型号/步进、扩展系列/型号、修订。
- 中部实时区：核心速度、倍频、总线速度。
- 右侧缓存区：L1 Data、L1 Inst、L2、L3，带容量和路数。
- 底部选择区：处理器选择、核心数、线程数。
- 指令集是一整行长文本，适合复制和快速确认 SIMD 能力。

它的不足是视觉比较旧，字段拥挤，解释性较弱，不适合直接嵌入 HwScope 的 Fluent shell。

### AIDA64 CPUID

AIDA64 CPUID 的优点是单页诊断面板更清晰：

- 左侧身份字段，右侧品牌图和关键数值。
- 频率、电压、缓存、指令集、主板、BIOS、芯片组、集显、内存类型放在同一张诊断图里。
- 底部有 CPU/Core/SMT unit 选择，可切换观察当前核心。
- 页面适合截图和保存。

它的不足是绿色专属视觉风格不适合 HwScope，部分字段依赖 AIDA64 自身硬件库，不能简单照搬到当前 WMI-only Core。

## Product Scope

CPU 详情页分三层交付。

### Stage 1: Structured CPU Detail

第一阶段目标是做出可用的 CPU 详情页，并尽量使用 Windows 标准 API / WMI / CPUID 可获得的字段。

必须展示：

- CPU 名称和完整规格名称。
- Vendor，例如 `AuthenticAMD` / `GenuineIntel`。
- Code name 或平台代号。能识别则显示，不能识别则显示 `未识别`。
- Package / socket，例如 `Socket FP7`。
- Process，例如 `4 nm`。能从映射表识别则显示，不能识别则留空。
- Family、Model、Stepping、Extended Family、Extended Model、Revision。
- 核心数、线程数、处理器数量。
- 当前核心频率、最大频率、总线频率、倍频。
- L1D、L1I、L2、L3 缓存容量；能拿到 associativity 时显示路数。
- 指令集列表。
- 主板、BIOS、芯片组、集成显卡、内存类型作为 CPU 上下文字段。

必须支持：

- 主窗口左侧 `硬件 -> 中央处理器 (CPU)` 进入页面。
- 刷新按钮。
- 复制 CPU 详情为文本。
- 保存为文本或后续报告入口的占位按钮。
- 字段级不可用状态，例如 `未识别`、`不支持`、`需要 native CPUID worker`。

### Stage 2: Windows Topology And Cache

第二阶段引入 Windows `GetLogicalProcessorInformationEx`，补足 WMI 拿不到或不可靠的 OS 拓扑和缓存共享字段。

目标字段：

- CPU groups、NUMA node、package/core/logical processor 拓扑。
- 每个物理核心对应哪些 logical processors。
- SMT 是否开启。
- Efficiency class，后续用于 Intel P-core / E-core 区分。
- Cache topology，包括 cache type、size、line size、ways、shared logical processors。

当前状态：已实现。Native CPUID 不属于 Stage 2 的完成条件。

### Stage 3: Native CPUID Worker

第三阶段引入 native CPUID worker 或复用扩展 native module，补足 WMI 和 Windows topology API 拿不到的 CPU 原始身份与 feature flags。

目标字段：

- CPUID leaf 原始 family/model/stepping/ext family/ext model。
- 标准和扩展 feature flags。
- Brand string、vendor string。
- CPUID cache leaf 中的 partitions、sets 等更底层字段。
- Hybrid topology 的 CPUID 侧信息。

### Stage 4: Live Sampling

第四阶段做实时监控性质的字段刷新。

目标能力：

- 当前核心频率定时刷新。
- 每核心频率表。
- 当前选中 core / SMT unit。
- 温度、电压、功耗等传感器字段。此阶段可能需要 LibreHardwareMonitor、OpenHardwareMonitor 类库，或自研驱动/服务。

## Information Architecture

页面采用一屏优先、可滚动补充的结构。桌面宽度下使用两列信息面板，保证高密度；窄宽度时降为单列。

```text
CPU Detail Page
  Header
    CPU name, vendor, code name, status chips, refresh/copy/save actions

  Overview Band
    Identity table
    CPU badge / compact platform summary

  Main Grid
    Specification
    Clocks
    Topology
    Cache
    Instruction Set
    Platform Context

  Footer / Diagnostics
    Data source, last refresh time, missing-field notes
```

## Field Groups

### Header

用途：一眼确认正在看的 CPU。

字段：

- Display Name: `AMD Ryzen 7 8745H`
- Full Name: `AMD Ryzen 7 8745H w/ Radeon 780M Graphics`
- Vendor: `AuthenticAMD`
- Code Name: `Hawk Point`
- Status Chips:
  - `8C / 16T`
  - `DDR5-5600`
  - `4 nm`
  - `Socket FP7`

动作：

- Refresh
- Copy
- Save

### Specification

对应 CPU-Z 顶部 `处理器` 区和 AIDA64 上半部分。

字段：

| Label | Example | Source Strategy |
| --- | --- | --- |
| Name | AMD Ryzen 7 | CPUID brand/WMI parsed |
| Code Name | Hawk Point | local mapping by model |
| Specification | AMD Ryzen 7 8745H w/ Radeon 780M Graphics | CPUID brand string / WMI Name |
| Package | Socket FP7 | mapping / future native topology |
| Technology | 4 nm | mapping |
| TDP | 45 W | mapping, optional |
| Core Voltage | 1.281 V | sensor stage, optional |
| Vendor | AuthenticAMD | CPUID |
| Family | F | CPUID |
| Model | 5 | CPUID |
| Stepping | 2 | CPUID |
| Ext. Family | 19 | CPUID |
| Ext. Model | 75 | CPUID |
| Revision | HPT1-A2 | mapping |

第一版如果没有 native CPUID，Family/Model/Stepping 可先从 `Win32_Processor` 的 `Family`、`Revision`、`Stepping` 尝试补齐，但要标注置信度较低。

### Clocks

对应 CPU-Z `时钟` 区和 AIDA64 `CPU Clock` 区。

字段：

- Current Core Clock
- Multiplier
- Bus Clock
- Base Clock
- Max Clock
- Effective Clock, future

第一版策略：

- `MaxClockSpeed` 来自 WMI。
- 当前频率可尝试 `Win32_PerfFormattedData_Counters_ProcessorInformation` 或 Windows performance counter。
- 倍频可由 `currentClock / busClock` 推导，推导值要标记为 computed。
- Bus Clock 第一版可显示 `未识别`，或在 AMD/Intel 常见平台用 `100.0 MHz` 作为低置信度推导时必须标注。

刷新策略：

- 页面打开时采集一次。
- 点击 Refresh 采集一次。
- 后续 live mode 再做 1s 周期刷新，不在第一版默认启用。

### Topology

字段：

- Physical Packages
- Physical Cores
- Logical Processors
- SMT Enabled
- CPU Groups
- NUMA Nodes
- Selected Core / Logical Processor, future
- Hybrid Core Layout, future

第一版来源：

- WMI `Win32_Processor.NumberOfCores`
- WMI `Win32_Processor.NumberOfLogicalProcessors`
- Windows `GetLogicalProcessorInformationEx`, recommended next step

### Cache

对应 CPU-Z 右侧缓存区和 AIDA64 L1/L2/L3 区。

字段：

| Label | Example | Extra |
| --- | --- | --- |
| L1 Data | 8 x 32 KB | 8-way |
| L1 Instruction | 8 x 32 KB | 8-way |
| L2 | 8 x 1 MB | 8-way |
| L3 | 16 MB | 16-way |
| Line Size | 64 B | future |
| Shared By | 16 threads | future |

推荐采集：

- 首选 native CPUID cache leaf。
- 其次 Windows `GetLogicalProcessorInformationEx(RelationCache)`。
- 不建议长期依赖硬编码映射，但第一版可以对常见 CPU 做 fallback mapping，并在 diagnostics 标出来源。

### Instruction Set

对应 CPU-Z / AIDA64 的长文本字段。

展示方式：

- 默认展示为可换行 token chips，便于扫描。
- 提供 `Copy as text`。
- 支持分组：
  - Basic: x86, x86-64
  - SIMD: MMX, SSE, SSE2, SSE3, SSSE3, SSE4.1, SSE4.2, SSE4A, AVX, AVX2, AVX-512
  - Crypto: AES, SHA
  - Virtualization: VT-x, AMD-V
  - Security: NX, SMEP, SMAP, SEV, TME, future

第一版可先用单行/多行文本，待 native CPUID worker 完成后再做完整分组。

### Platform Context

AIDA64 把 CPU 放在主板和内存上下文里，这对排查很有价值。HwScope CPU 页面也应保留这块。

字段：

- Motherboard
- BIOS Version
- Chipset
- Integrated Video
- Memory Type
- Memory Clock
- DRAM:FSB Ratio

第一版来源：

- Motherboard 来自现有 `Win32_BaseBoard`。
- BIOS 来自 `Win32_BIOS`。
- Memory Type / Clock 来自 `Win32_PhysicalMemory`。
- Chipset、Integrated Video、DRAM:FSB Ratio 可先留空或低置信度推导。

## Visual Design

CPU 页面应保持 HwScope 当前设计语言：WPF-UI / Fluent、动态主题 token、紧凑硬件工具布局。

### Layout

推荐桌面布局：

```text
---------------------------------------------------------------
CPU Details                                [Refresh] [Copy] [Save]
AMD Ryzen 7 8745H w/ Radeon 780M Graphics
[AuthenticAMD] [8C/16T] [Socket FP7] [4 nm] [DDR5-5600]

---------------------------------------------------------------
| Specification                         | Live Clock           |
| Name              AMD Ryzen 7         | Core Clock 3778 MHz  |
| Code Name         Hawk Point          | Multiplier x37.8     |
| Package           Socket FP7          | Bus Clock 99.96 MHz  |
| Technology        4 nm                | Max Clock 3800 MHz   |
| ...                                   |                      |
---------------------------------------------------------------
| Topology                              | Cache                |
| Packages          1                   | L1D 8 x 32 KB 8-way |
| Cores             8                   | L1I 8 x 32 KB 8-way |
| Threads           16                  | L2  8 x 1 MB 8-way  |
| CPU Groups        1                   | L3  16 MB 16-way    |
---------------------------------------------------------------
| Instruction Set                                             |
| [x86] [x86-64] [SSE] [SSE2] [AVX] [AVX2] [AVX-512] ...       |
---------------------------------------------------------------
| Platform Context                                            |
| Motherboard ...                                             |
| BIOS Version ...                                            |
| Memory Type ...                                             |
---------------------------------------------------------------
```

### Component Choices

- Use a normal `Page` / `UserControl`, not a modal window.
- Use page-level full-width bands or unframed grids, not nested cards.
- Use cards only for repeated compact groups if needed. Border radius should stay at 7-8 px, consistent with existing summary cards.
- Use icon buttons for actions where obvious:
  - Refresh: `ArrowClockwise24`
  - Copy: `Copy24`
  - Save: `Save24`
- Use text labels for hardware fields because field names matter.
- Use `DynamicResource` for all app colors.

### Density

CPU tools are diagnostic tools, so the page should be dense but not cramped.

Recommended sizing:

- Page title: 24 px.
- Section title: 14-15 px semibold.
- Field label: 13 px.
- Field value: 13-14 px.
- Row height: 28-32 px.
- Two-column grid on width >= 1100 px.
- Single-column stacked layout below 900 px.

### Missing Values

Never leave a cell visually blank unless the field is intentionally absent.

Use:

- `未识别`: attempted but unavailable.
- `不支持`: hardware/API does not support the field.
- `待接入 native CPUID`: requires future worker.
- `推导值`: show as a small source badge when value is computed.

## Interaction Design

### Navigation

Current navigation item:

```xml
中央处理器 (CPU)
```

should route to the new CPU detail page instead of the summary page.

### Refresh

Refresh should:

- Disable itself while collection is running.
- Keep previous values visible until new data arrives.
- Update `Last refreshed` timestamp.
- Show a compact status message in the main window status bar if enabled.

### Copy

Copy should produce a stable text report:

```text
CPU
Processor: AMD Ryzen 7 8745H w/ Radeon 780M Graphics
Code Name: Hawk Point
Vendor: AuthenticAMD
Package: Socket FP7
Technology: 4 nm
Cores / Threads: 8 / 16
...
Instruction Set: x86, x86-64, SSE, SSE2, ...
Generated At: 2026-06-26 11:30:00
```

### Save

First implementation may save `.txt`; future report system can support JSON/PDF.

### Core Selector

The AIDA64 bottom selector is useful but should be deferred until live per-core data exists.

Future selector:

```text
CPU #1 / Core #1 / SMT Unit #1
```

Selecting a core updates current clock and future sensor rows, not static identity fields.

## Data Model Proposal

Add new Core models instead of expanding `HardwareReport`.

```csharp
public sealed record CpuDetailReport(
    CpuIdentity Identity,
    CpuSpecification Specification,
    CpuTopology Topology,
    CpuClockInfo Clocks,
    IReadOnlyList<CpuCacheInfo> Caches,
    IReadOnlyList<CpuFeature> Features,
    CpuPlatformContext Platform,
    IReadOnlyList<CpuDataNote> Notes,
    DateTimeOffset GeneratedAt);
```

Suggested records:

```csharp
public sealed record CpuIdentity(
    string DisplayName,
    string SpecificationName,
    string Vendor,
    string CodeName);

public sealed record CpuSpecification(
    string Package,
    string Technology,
    string Tdp,
    string CoreVoltage,
    string Family,
    string Model,
    string Stepping,
    string ExtendedFamily,
    string ExtendedModel,
    string Revision);

public sealed record CpuTopology(
    int PackageCount,
    int CoreCount,
    int LogicalProcessorCount,
    bool? SmtEnabled,
    int? CpuGroupCount,
    int? NumaNodeCount);

public sealed record CpuClockInfo(
    double? CurrentMHz,
    double? BaseMHz,
    double? MaxMHz,
    double? BusMHz,
    double? Multiplier);

public sealed record CpuCacheInfo(
    CpuCacheLevel Level,
    string Name,
    int? InstanceCount,
    long? SizeBytes,
    int? Ways,
    int? LineSizeBytes,
    int? SharedLogicalProcessorCount);

public sealed record CpuFeature(
    string Name,
    CpuFeatureGroup Group,
    bool IsSupported);

public sealed record CpuPlatformContext(
    string Motherboard,
    string BiosVersion,
    string Chipset,
    string IntegratedVideo,
    string MemoryType,
    string MemoryClock,
    string DramFsbRatio);
```

Important: each field should eventually carry source/confidence metadata. If that is too heavy for Stage 1, keep `CpuDataNote` as the place to disclose fallback and missing values.

## Collection Strategy

### Stage 1 Sources

Use existing Core patterns:

- WMI through `HwScope.Core.Windows.Wmi`.
- Add `CpuDetailCollector` under `HwScope.Core.Hardware`.
- Keep `HardwareCollector` for summary; do not overload it with detailed CPU logic.

WMI classes to evaluate:

- `Win32_Processor`
- `Win32_ComputerSystem`
- `Win32_BaseBoard`
- `Win32_BIOS`
- `Win32_PhysicalMemory`
- `Win32_VideoController`
- `Win32_PerfFormattedData_Counters_ProcessorInformation`

Windows API to evaluate:

- `GetLogicalProcessorInformationEx`

### Stage 2 Sources

Use Windows topology API:

- `GetLogicalProcessorInformationEx(RelationAll)`
- `RelationProcessorCore`
- `RelationCache`
- `RelationProcessorPackage`
- `RelationGroup`
- `RelationNumaNode`

Keep `(Group, Mask)` through the model instead of flattening processor indexes.

### Stage 3 Sources

Add a native CPUID worker or library. For consistency with memory benchmark, a process worker is acceptable:

```text
HwScope.Core
  CpuDetailCollector
  ICpuIdProvider
  NativeCpuIdProcessProvider

HwScope.Native.CpuId
  cpuid.exe --json
```

Preferred worker output is JSON, not CSV, because CPU detail data is nested and versioned.

```json
{
  "schemaVersion": 1,
  "vendor": "AuthenticAMD",
  "brand": "AMD Ryzen 7 8745H w/ Radeon 780M Graphics",
  "family": 25,
  "model": 117,
  "stepping": 2,
  "features": ["sse", "sse2", "avx", "avx2"],
  "caches": []
}
```

## Error Handling

CPU detail collection should be best-effort:

- If WMI fails, still render page with unavailable fields.
- If native CPUID worker is missing, show WMI-backed fields and add a note.
- If native worker exits non-zero, surface a concise status message and log diagnostic details.
- Do not block the entire page because one field group failed.

## Performance

The current summary page collects hardware synchronously on the UI thread. CPU detail page should avoid repeating that pattern.

Requirements:

- Collection runs asynchronously.
- UI remains responsive.
- Refresh is cancellable or at least ignores stale results.
- Slow source groups can be loaded independently later.

Recommended collection shape:

```text
Load shell immediately
  -> show cached/summary CPU name if available
  -> collect WMI details in background
  -> collect native CPUID details if worker exists
  -> merge and update sections
```

## Implementation Plan

### Milestone 1: Documentation And Shell Route

- Add this design document.
- Add a `CpuDetailPage` placeholder route under `硬件 -> 中央处理器 (CPU)`.
- Keep summary page unchanged.

### Milestone 2: WMI-Backed CPU Detail

- Add `CpuDetailReport` models.
- Add `CpuDetailCollector`.
- Add formatter for copy/export.
- Implement CPU detail page with specification, topology, clocks, and platform context.

### Milestone 3: Windows Topology, Cache And Core Mapping

- Add `GetLogicalProcessorInformationEx` cache/topology provider.
- Replace placeholder/mapped topology and cache rows with API data.
- Add core-to-logical-processor mapping.

### Milestone 4: Native CPUID Features

- Add feature list from native CPUID worker or a minimal managed/native bridge.
- Replace mapped instruction rows with real CPUID data.
- Replace WMI family/model/stepping with raw CPUID values.

### Milestone 5: Live Core Data

- Add core selector.
- Add current frequency refresh.
- Prepare sensor integration for voltage/temperature/power.

## Acceptance Criteria

For Stage 1:

- Clicking `硬件 -> 中央处理器 (CPU)` opens CPU detail content, not the generic summary page.
- The page displays at least identity, specification, topology, clocks, instruction set placeholder/known values, and platform context.
- Missing fields are labeled clearly.
- Refresh does not freeze the UI.
- Copy produces a readable CPU report.
- `dotnet build` passes.

For Stage 2:

- Windows API topology values are preferred over WMI when available.
- Cache fields no longer rely on hardcoded CPU-specific text when Windows API succeeds.
- Core mapping is visible in the page and included in exported text.
- The page can disclose data source for values that are mapped or computed.

For Stage 3:

- Native CPUID data is versioned JSON.
- Feature list no longer relies on hardcoded CPU-specific text.
- CPUID worker missing/failure is non-fatal.
