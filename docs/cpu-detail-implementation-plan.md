# CPU Detail Page Implementation Plan

本文档是 `cpu-detail-page-design.md` 的工程落地方案，面向实际开发。目标是在不破坏当前 HwScope 架构的前提下，逐步实现主窗口里的 CPU 详情页，并为后续 native CPUID、实时频率、传感器和报告导出留出稳定扩展点。

## Current Baseline

当前项目相关状态：

- `HwScope.Core.Hardware.HardwareCollector` 提供硬件摘要字符串。
- `HwScope.Core.Hardware.Cpu` 已提供 CPU 详情模型、WMI 采集、Windows topology API 聚合和文本报告格式化。
- `HardwareReport` 是展示模型，不适合承载 CPU 详情。
- `MainWindow` 的 `中央处理器 (CPU)` 导航项已路由到 `CpuDetailPage`。
- `HardwareSummaryPage` 在 UI 线程同步调用 WMI。
- `MemoryBenchmarkProcessRunner` 已经建立了 “Core 调 native worker 进程” 的先例，但 CPU 详情尚无 native worker。
- 主题系统已经支持 `DynamicResource`，新页面应复用 `HwScopePanelBrush`、`HwScopeContentBrush`、`HwScopeCardBrush`、`HwScopeLineBrush`、`HwScopeTextBrush` 等 token。

实现策略：

1. 已先用 WMI + Windows API 做结构化 CPU 页面，不阻塞在 native CPUID worker。
2. 第一版不要改 `HardwareReport` 语义，新增 CPU 详情模型。
3. 第一版 UI 必须异步加载，避免继续扩大 UI 线程 WMI 阻塞问题。
4. 所有不可靠、推导、待接入字段必须明确标注来源或状态。

## Target Architecture

新增 CPU 详情后的架构：

```text
HwScope.App
  MainWindow
    routes "cpu-detail" -> CpuDetailPage

  Pages/CpuDetailPage.xaml
  Pages/CpuDetailPage.xaml.cs
    owns UI state, async refresh, copy/save

HwScope.Core
  Hardware/Cpu/
    CpuDetailReport.cs
    CpuDetailCollector.cs
    CpuDetailReportFormatter.cs
    CpuKnownProcessorCatalog.cs
    CpuFeatureCatalog.cs
    CpuFieldValue.cs

  Windows/
    Wmi.cs
    LogicalProcessorInformation.cs

Future:
  HwScope.Native.CpuId/cpuid.exe
  HwScope.Core.Hardware.Cpu.NativeCpuIdProcessProvider
```

Namespace recommendation:

```csharp
namespace HwScope.Core.Hardware.Cpu;
```

This avoids making `HwScope.Core.Hardware` too crowded while keeping CPU detail under the hardware domain.

## Milestone 1: WMI-Backed CPU Detail Models

### Files To Add

```text
src/HwScope.Core/Hardware/Cpu/CpuDetailReport.cs
src/HwScope.Core/Hardware/Cpu/CpuDataSource.cs
src/HwScope.Core/Hardware/Cpu/CpuFieldValue.cs
src/HwScope.Core/Hardware/Cpu/CpuCacheLevel.cs
src/HwScope.Core/Hardware/Cpu/CpuFeatureGroup.cs
```

### Model Shape

Use typed section records, but wrap individual values when the UI needs source and availability metadata.

Recommended generic value:

```csharp
public sealed record CpuFieldValue<T>(
    T? Value,
    string DisplayText,
    CpuDataSource Source,
    bool IsAvailable,
    bool IsEstimated = false,
    string? Note = null);
```

Recommended source enum:

```csharp
public enum CpuDataSource
{
    Unknown,
    Wmi,
    WindowsApi,
    Cpuid,
    Mapping,
    Computed,
    Placeholder
}
```

For UI simplicity, every field should already have display text. The page should not need to know how to format bytes, MHz, or unavailable states.

Top-level report:

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

Concrete section records can start pragmatic:

```csharp
public sealed record CpuIdentity(
    CpuFieldValue<string> DisplayName,
    CpuFieldValue<string> SpecificationName,
    CpuFieldValue<string> Vendor,
    CpuFieldValue<string> CodeName);

public sealed record CpuSpecification(
    CpuFieldValue<string> Package,
    CpuFieldValue<string> Technology,
    CpuFieldValue<string> Tdp,
    CpuFieldValue<string> CoreVoltage,
    CpuFieldValue<string> Family,
    CpuFieldValue<string> Model,
    CpuFieldValue<string> Stepping,
    CpuFieldValue<string> ExtendedFamily,
    CpuFieldValue<string> ExtendedModel,
    CpuFieldValue<string> Revision);

public sealed record CpuTopology(
    CpuFieldValue<int> PackageCount,
    CpuFieldValue<int> CoreCount,
    CpuFieldValue<int> LogicalProcessorCount,
    CpuFieldValue<bool> SmtEnabled,
    CpuFieldValue<int> CpuGroupCount,
    CpuFieldValue<int> NumaNodeCount);

public sealed record CpuClockInfo(
    CpuFieldValue<double> CurrentMHz,
    CpuFieldValue<double> BaseMHz,
    CpuFieldValue<double> MaxMHz,
    CpuFieldValue<double> BusMHz,
    CpuFieldValue<double> Multiplier);
```

For features, keep a simple list:

```csharp
public sealed record CpuFeature(
    string Name,
    CpuFeatureGroup Group,
    bool IsSupported,
    CpuDataSource Source);
```

### Helper Constructors

Add helpers to keep collector code readable:

```csharp
public static class CpuField
{
    public static CpuFieldValue<string> Text(string? value, CpuDataSource source, string unavailable = "未识别");
    public static CpuFieldValue<int> Number(uint value, CpuDataSource source, string unavailable = "未识别");
    public static CpuFieldValue<double> MHz(double? value, CpuDataSource source);
    public static CpuFieldValue<T> Placeholder<T>(string text = "待接入 native CPUID");
}
```

Why this matters: CPU detail has many fields. Without helpers, every collector method will repeat unavailable formatting and source flags.

## Milestone 2: CpuDetailCollector

### Files To Add

```text
src/HwScope.Core/Hardware/Cpu/CpuDetailCollector.cs
src/HwScope.Core/Hardware/Cpu/CpuKnownProcessorCatalog.cs
src/HwScope.Core/Hardware/Cpu/CpuMemoryTypeFormatter.cs
```

### Public API

```csharp
public sealed class CpuDetailCollector
{
    public CpuDetailReport Collect();
}
```

Keep it synchronous in Core at first, matching current `HardwareCollector`. App will call it through `Task.Run` to keep the UI responsive. Later, if source groups become truly async, add `CollectAsync`.

### Collection Flow

```text
Collect()
  cpu = first Win32_Processor
  processors = all Win32_Processor
  board = first Win32_BaseBoard
  bios = first Win32_BIOS
  memoryModules = all Win32_PhysicalMemory
  videoControllers = all Win32_VideoController
  perfClock = query current processor frequency if available
  topology = WMI now, Windows API later
  knownInfo = CpuKnownProcessorCatalog.Match(cpu.Name)

  Build Identity
  Build Specification
  Build Topology
  Build Clocks
  Build Cache placeholders/mapped values
  Build Features placeholders/mapped values
  Build Platform Context
  Build Notes
```

### WMI Queries

Use these first:

```sql
SELECT Name, Manufacturer, Description, NumberOfCores, NumberOfLogicalProcessors,
       MaxClockSpeed, CurrentClockSpeed, SocketDesignation, ProcessorId,
       Architecture, Family, Revision, Stepping
FROM Win32_Processor
```

```sql
SELECT Manufacturer, Product FROM Win32_BaseBoard
```

```sql
SELECT SMBIOSBIOSVersion, Version, ReleaseDate FROM Win32_BIOS
```

```sql
SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, MemoryType
FROM Win32_PhysicalMemory
```

```sql
SELECT Name, AdapterRAM, PNPDeviceID FROM Win32_VideoController
```

Optional current clock:

```sql
SELECT Name, PercentProcessorPerformance, ProcessorFrequency
FROM Win32_PerfFormattedData_Counters_ProcessorInformation
WHERE Name = '_Total'
```

Important: some systems do not expose `ProcessorFrequency`. The code should treat it as optional.

### Fields That Can Be Real In Stage 1

Likely reliable:

- SpecificationName from `Win32_Processor.Name`
- CoreCount from `NumberOfCores`
- LogicalProcessorCount from `NumberOfLogicalProcessors`
- MaxMHz from `MaxClockSpeed`
- CurrentMHz from `CurrentClockSpeed` or perf counter when available
- SocketDesignation from WMI, if populated
- Motherboard from `Win32_BaseBoard`
- BIOS version from `Win32_BIOS`
- Memory type and configured clock from `Win32_PhysicalMemory`
- Integrated video from `Win32_VideoController`, with low-confidence heuristics

Partially reliable:

- Vendor from WMI `Manufacturer`; normalize to CPUID-style where possible:
  - `AuthenticAMD` for AMD
  - `GenuineIntel` for Intel
- Family/Revision/Stepping from WMI; these are not identical to raw CPUID on all systems.
- BusMHz and Multiplier computed from current/max clock, only if enough inputs exist.

Not reliable without CPUID/native or mapping:

- CodeName
- Technology
- TDP
- Extended Family
- Extended Model
- Revision marketing string, such as `HPT1-A2`
- Instruction set
- Cache associativity and detailed cache topology
- Core voltage

### Known Processor Catalog

Add a small catalog for the developer machine and easy expansion, but never make the page depend on it for correctness.

```csharp
public sealed record CpuKnownProcessorInfo(
    string MatchText,
    string DisplayName,
    string CodeName,
    string Package,
    string Technology,
    string Tdp,
    string Revision,
    IReadOnlyList<CpuCacheInfo> Caches,
    IReadOnlyList<CpuFeature> Features);
```

Initial entry can cover:

```text
AMD Ryzen 7 8745H
CodeName: Hawk Point
Package: Socket FP7/FP7r2
Technology: 4 nm
TDP: 45 W
Revision: HPT1-A2
Cache:
  L1D 8 x 32 KB
  L1I 8 x 32 KB
  L2 8 x 1 MB
  L3 16 MB
Features:
  x86, x86-64, MMX, MMX+, SSE, SSE2, SSE3, SSSE3,
  SSE4.1, SSE4.2, SSE4A, AVX, AVX2, AVX-512, FMA, AES, SHA
```

Every value from this catalog must use `CpuDataSource.Mapping` and `IsEstimated = true` unless verified by CPUID.

### Formatter

Add:

```text
src/HwScope.Core/Hardware/Cpu/CpuDetailReportFormatter.cs
```

Responsibilities:

- Format stable text for copy.
- Format report sections in a deterministic order.
- Include source notes when values are mapped/computed/placeholder.

Do not put formatting logic in the WPF page.

## Milestone 3: Windows Topology API

### File To Add

```text
src/HwScope.Core/Windows/LogicalProcessorInformation.cs
```

Purpose: wrap `GetLogicalProcessorInformationEx` behind a small Core API.

Status: complete in Stage 2.

Implemented internal shape:

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
    IReadOnlyList<LogicalPackageInfo> Packages,
    IReadOnlyList<LogicalNumaNodeInfo> NumaNodes);
```

Cache shape:

```csharp
internal sealed record LogicalCacheInfo(
    byte Level,
    LogicalCacheType Type,
    long SizeBytes,
    int LineSizeBytes,
    int Associativity,
    LogicalProcessorMask Mask);
```

Implementation notes:

- Use P/Invoke only in `HwScope.Core.Windows`.
- Keep unsafe/marshalling code isolated.
- If API fails, return `null` and let `CpuDetailCollector` fall back to WMI/mapping.
- This API can provide package/core/cache data without needing CPUID first.

Development order recommendation:

1. Implement CPU page first with WMI + placeholders.
2. Add topology API in a separate change.
3. Replace cache placeholders with topology API data.

Reason: P/Invoke structure parsing is riskier than the rest of the page. Keeping it isolated reduces blast radius.

## Milestone 4: CpuDetailPage UI

### Files To Add

```text
src/HwScope.App/Pages/CpuDetailPage.xaml
src/HwScope.App/Pages/CpuDetailPage.xaml.cs
```

Optional lightweight UI records in code-behind:

```csharp
public sealed record CpuFieldRow(string Label, string Value, string Source, bool IsEstimated);
public sealed record CpuSection(string Title, IReadOnlyList<CpuFieldRow> Rows);
```

Avoid a full MVVM framework for now because the existing app is code-behind based. Keep the page self-contained and consistent with `HardwareSummaryPage`.

### Page Responsibilities

- Trigger initial async load on first `Loaded`.
- Allow manual refresh.
- Display stale values until new load completes.
- Disable Refresh/Copy/Save appropriately.
- Raise `StatusChanged` event to `MainWindow`.
- Copy formatted CPU report through `CpuDetailReportFormatter`.

### Async Pattern

Recommended code-behind pattern:

```csharp
private readonly CpuDetailCollector _collector = new();
private int _refreshVersion;
private bool _isBusy;
private CpuDetailReport? _currentReport;

public async Task RefreshAsync()
{
    var version = Interlocked.Increment(ref _refreshVersion);
    SetBusy(true);

    try
    {
        var report = await Task.Run(_collector.Collect).ConfigureAwait(true);
        if (version != _refreshVersion)
        {
            return;
        }

        _currentReport = report;
        Render(report);
        SetStatus("CPU 详情已刷新。");
    }
    catch (Exception ex)
    {
        SetStatus($"CPU 详情刷新失败：{ex.Message}");
        MessageBox.Show(...);
    }
    finally
    {
        if (version == _refreshVersion)
        {
            SetBusy(false);
        }
    }
}
```

This ignores stale results if a user clicks refresh repeatedly. A `CancellationTokenSource` can be added later, but WMI cancellation is not always clean.

### XAML Layout

Use the page's existing design tokens:

```xml
<Grid Background="{DynamicResource HwScopeContentBrush}">
```

Top-level structure:

```text
Grid
  Row 0: Header/action bar
  Row 1: ScrollViewer
    StackPanel
      Overview band
      Two-column section grid
      Instruction set band
      Platform context band
      Notes band
```

Recommended section component:

```text
Border
  Background HwScopeCardBrush
  BorderBrush HwScopeLineBrush
  CornerRadius 7
  Grid rows:
    Section title
    ItemsControl rows
```

This technically uses cards for section groups. That is acceptable here because the page is a diagnostic tool with grouped field panels, not a marketing layout. Avoid placing cards inside cards.

### Field Row Layout

Use a stable two-column row:

```xml
<Grid MinHeight="30">
  <Grid.ColumnDefinitions>
    <ColumnDefinition Width="150" />
    <ColumnDefinition Width="*" />
    <ColumnDefinition Width="Auto" />
  </Grid.ColumnDefinitions>
</Grid>
```

Third column can show a small source badge:

- `WMI`
- `API`
- `推导`
- `映射`
- `待接入`

Keep badges visually quiet.

### Instruction Features

Stage 1:

- Use an `ItemsControl` with `WrapPanel`.
- Each feature token is a small bordered `TextBlock`.
- If features are from mapping, show a note: `指令集来自处理器型号映射，后续将由 native CPUID 校验。`

### Empty / Loading States

Initial load:

- Header title: `CPU Details`
- Subtitle: `正在读取 CPU 信息...`
- Sections can show skeleton-like rows with `读取中...`, or simply one loading text.

Failure:

- Keep page visible.
- Show `CPU 信息读取失败` and error message.
- Keep Refresh available.

## Milestone 5: Navigation Integration

### MainWindow.xaml

Change CPU nav item:

```xml
<ui:NavigationViewItem Content="中央处理器 (CPU)"
                       Tag="cpu-detail"
                       Icon="{ui:SymbolIcon DeveloperBoard20}" />
```

### MainWindow.xaml.cs

Add field:

```csharp
private readonly CpuDetailPage _cpuDetailPage = new();
```

Wire status:

```csharp
_cpuDetailPage.StatusChanged += (_, status) => SetFooterStatus(status);
```

Switch route:

```csharp
case "cpu-detail":
    ShowCpuDetail();
    break;
```

Add method:

```csharp
private void ShowCpuDetail()
{
    PageHost.Content = _cpuDetailPage;
    SetFooterStatus("CPU 详情。");
}
```

Toolbar `内存` currently routes to summary. Leave unrelated toolbar behavior unchanged unless the same change introduces dedicated memory page.

## Milestone 6: Save / Export

Stage 1 can implement copy only and leave Save disabled, or implement simple `.txt` save.

If implementing Save now:

- Use WPF `SaveFileDialog`.
- Default filename: `HwScope-CPU-yyyyMMdd-HHmmss.txt`.
- Content from `CpuDetailReportFormatter.Format`.

Recommended first pass: enable Copy, keep Save disabled with tooltip/status `报告导出后续接入`. This avoids pulling file dialog behavior into the first UI implementation if not needed.

## Milestone 7: Native CPUID Worker

Native CPUID is the next CPU data stage after the WMI-backed page and Windows topology API are in place.

### Project Shape

```text
src/HwScope.Native.CpuId/
  CMakeLists.txt
  README.md
  scripts/build-msvc.ps1
  src/main.cpp
```

Use a separate native executable from `membench.exe`. CPU identity collection is fast and structurally different from benchmark work, so combining them would muddy responsibilities.

### CLI Contract

```powershell
cpuid.exe --json
```

Output:

```json
{
  "schemaVersion": 1,
  "vendor": "AuthenticAMD",
  "brand": "AMD Ryzen 7 8745H w/ Radeon 780M Graphics",
  "family": 25,
  "model": 117,
  "stepping": 2,
  "extendedFamily": 25,
  "extendedModel": 7,
  "features": [
    "x86-64",
    "sse",
    "sse2",
    "avx",
    "avx2"
  ],
  "caches": [
    {
      "level": 1,
      "type": "Data",
      "sizeBytes": 32768,
      "ways": 8,
      "lineSizeBytes": 64,
      "sharedLogicalProcessors": 1
    }
  ]
}
```

### Core Provider

Add:

```text
src/HwScope.Core/Hardware/Cpu/ICpuIdProvider.cs
src/HwScope.Core/Hardware/Cpu/NativeCpuIdProcessProvider.cs
src/HwScope.Core/Hardware/Cpu/NativeCpuIdResult.cs
```

API:

```csharp
public interface ICpuIdProvider
{
    CpuIdProviderResult TryCollect();
}
```

Use `TryCollect` rather than throwing for normal missing-worker cases. Missing worker is expected during development.

### Executable Resolution

Follow the memory runner pattern, but avoid machine-specific absolute paths.

Search:

```text
AppContext.BaseDirectory\cpuid.exe
AppContext.BaseDirectory\native\cpuid.exe
repo-relative src\HwScope.Native.CpuId\build\Release\cpuid.exe
```

Do not add `C:\Users\...` fallback.

### Timeout

CPUID should complete almost instantly. Use a short timeout:

```text
2 seconds
```

If timeout occurs:

- Kill process.
- Add note.
- Continue with WMI values.

## Milestone 8: Tests And Validation

Current repo has no test project. For the first implementation, use build and manual/CLI validation. If adding tests, create:

```text
src/HwScope.Core.Tests/HwScope.Core.Tests.csproj
```

Recommended testable units:

- `CpuKnownProcessorCatalog.Match`
- `CpuDetailReportFormatter.Format`
- memory type formatter
- field value helpers
- native CPUID JSON parser for Stage 3

Manual validation checklist:

- `dotnet build`
- Run GUI and click `硬件 -> 中央处理器 (CPU)`.
- Confirm page loads without blocking window interactions.
- Confirm Refresh disables and re-enables.
- Confirm Copy puts readable CPU report on clipboard.
- Confirm missing native CPUID fields say `待接入 native CPUID` or `未识别`.
- Toggle light/dark theme and verify text/background still readable.
- Resize window near minimum width and verify rows do not overlap.
- Run CLI `--json` to confirm existing summary path unchanged.

Optional validation commands:

```powershell
dotnet build
dotnet run --project .\src\HwScope.Cli -- --json
```

## Recommended Commit Sequence

Keep changes reviewable:

1. Add Core CPU models and formatter.
2. Add WMI-backed `CpuDetailCollector`.
3. Add `CpuDetailPage` UI and route navigation.
4. Add topology API/cache enrichment.
5. Add native CPUID worker and provider.
6. Enable save/export if desired.

This order keeps the page useful early while isolating risky native/PInvoke work.

## Implementation Risks

### WMI Field Quality

WMI values vary by vendor and firmware. `Family`, `Revision`, `SocketDesignation`, and current clock may be missing or semantically different from CPUID. The UI must disclose source and avoid pretending WMI is raw CPUID.

### Hardcoded CPU Catalog

A small mapping catalog is useful for the current AMD Ryzen 7 8745H machine, but it can become stale. Treat it as a fallback only. Native CPUID should replace it for feature flags and raw CPUID identity fields; Windows topology API should remain the preferred OS topology/cache-sharing source.

### UI Thread Blocking

The existing summary page blocks during WMI collection. The CPU detail page should not copy that pattern. Use `Task.Run` in the page even if Core collector is synchronous.

### P/Invoke Complexity

`GetLogicalProcessorInformationEx` requires careful buffer parsing. The Stage 2 implementation isolates all P/Invoke code in `HwScope.Core.Windows`.

### Native Worker Packaging

The memory benchmark already has a packaging gap. CPU native worker should include copy-to-output planning from the start, or at least avoid absolute user paths.

## Stage 1 Definition Of Done

Stage 1 is complete when:

- `CpuDetailReport` and related records exist under Core.
- `CpuDetailCollector` returns a best-effort report on the current machine.
- `CpuDetailReportFormatter` supports copy text.
- `CpuDetailPage` exists and is reachable from `中央处理器 (CPU)`.
- UI loads asynchronously and does not freeze during refresh.
- Page renders identity, specification, clocks, topology, cache placeholders/mapped values, features placeholders/mapped values, and platform context.
- Missing/mapped/computed values are visibly distinguishable.
- `dotnet build` succeeds.

## Stage 2 Definition Of Done

Stage 2 is complete when:

- `GetLogicalProcessorInformationEx` contributes topology/cache data.
- CPU page shows API-backed package/core/logical processor, SMT, CPU group and NUMA values.
- Cache rows show cache type, instance count, size, associativity, line size and shared logical processor count.
- Core-to-logical-processor mapping is available in UI and text export.
- API failure remains non-fatal, with WMI/mapping fallback.

Current status: complete.

## Stage 3 Definition Of Done

Stage 3 is complete when:

- Native CPUID worker returns schema-versioned JSON.
- Feature flags come from CPUID JSON rather than model-name mapping.
- CPUID worker missing/failure is non-fatal.
- Raw family/model/stepping/ext family/ext model come from CPUID.

## Stage 4 Definition Of Done

Stage 4 is complete when:

- The page has an optional live refresh mode.
- Current clock updates periodically.
- Core selector is meaningful.
- Sensor fields are backed by a real provider rather than placeholders.
