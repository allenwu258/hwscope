# Memory / SPD Detail Page Implementation Plan

本文档是 `memory-spd-detail-page-design.md` 的工程落地方案。目标是在现有 HwScope 架构下，用共享 hardware inventory 先做出一个真正可用的内存 / SPD 详情页，同时为后续 raw SPD reader、运行态内存控制器时序和传感器数据留出稳定扩展点。

## Current Baseline

当前项目已有能力：

- `HardwarePreloadService` 在启动期建立共享 `HardwareInventorySnapshot`。
- `HardwareSummaryPage` 和 `CpuDetailPage` 都从共享 snapshot 构建页面报告。
- `HardwareInventoryCollector` 已读取 `Win32_PhysicalMemory` 的基础字段：
  - `Capacity`
  - `Speed`
  - `ConfiguredClockSpeed`
  - `SMBIOSMemoryType`
  - `MemoryType`
- `HardwareCollector.CreateSummary()` 已能生成摘要页内存字符串。
- `CpuDetailCollector` 已有一份内存类型/频率格式化逻辑，但它是 CPU 平台上下文的一部分，不适合承载完整内存详情。
- 主窗口左侧 `硬件 -> 内存` 当前仍路由到 summary。
- 内存跑分窗口是独立窗口，使用 summary report 作为 header，不是内存详情页。

当前缺口：

- 没有 `HwScope.Core.Hardware.Memory` 领域模型。
- 没有 Memory/SPD 详情页。
- 没有每根内存模块的 manufacturer、part number、serial、slot、form factor、data width、total width、电压等字段。
- 没有 raw SPD 读取。
- 没有运行态当前时序读取。

## Target Architecture

第一版目标架构：

```text
HwScope.App
  MainWindow
    route "memory-detail" -> MemoryDetailPage

  Pages/MemoryDetailPage.xaml
  Pages/MemoryDetailPage.xaml.cs
    owns UI state, selected module, async refresh, copy/save

HwScope.Core
  Hardware/Inventory/
    HardwareInventorySnapshot.cs
      extended MemoryModuleSnapshot

    HardwareInventoryCollector.cs
      extended Win32_PhysicalMemory query

  Hardware/Memory/
    MemoryDataSource.cs
    MemoryFieldValue.cs
    MemoryDetailReport.cs
    MemoryTypeFormatter.cs
    MemoryDetailCollector.cs
    MemoryDetailReportFormatter.cs

Future:
  HwScope.Native.Spd/spd.exe
  HwScope.Core.Hardware.Memory.NativeSpdProcessProvider
  HwScope.Core.Hardware.Memory.MemoryControllerTimingProvider
```

设计原则：

- 页面不直接查询 WMI；只消费 `App.HardwarePreload`。
- Core 模型提供 display text 和 source metadata，UI 不负责解释字节、MHz、电压或 unavailable 状态。
- WMI/SMBIOS-backed 页面必须在没有 raw SPD worker 时仍然有用。
- SPD 和运行态 timing 是后续 provider，不阻塞第一版页面。
- 不复用 CPU detail 的类型，以免 memory domain 被 CPU domain 绑死；但可以复用其设计模式。

## Milestone 1: Inventory Enrichment

### Files To Modify

```text
src/HwScope.Core/Hardware/Inventory/HardwareInventorySnapshot.cs
src/HwScope.Core/Hardware/Inventory/HardwareInventoryCollector.cs
src/HwScope.Core/Hardware/HardwareCollector.cs
src/HwScope.Core/Hardware/Cpu/CpuDetailCollector.cs
```

### Extend MemoryModuleSnapshot

当前：

```csharp
public sealed record MemoryModuleSnapshot(
    ulong Capacity,
    uint Speed,
    uint ConfiguredClockSpeed,
    uint SmbiosMemoryType,
    uint MemoryType);
```

目标：

```csharp
public sealed record MemoryModuleSnapshot(
    ulong Capacity,
    uint Speed,
    uint ConfiguredClockSpeed,
    uint SmbiosMemoryType,
    uint MemoryType,
    string Manufacturer,
    string PartNumber,
    string SerialNumber,
    string BankLabel,
    string DeviceLocator,
    uint FormFactor,
    uint DataWidth,
    uint TotalWidth,
    uint ConfiguredVoltage,
    uint MinVoltage,
    uint MaxVoltage,
    uint MemoryTypeDetail,
    uint InterleavePosition,
    string Tag);
```

### WMI Query

Replace the memory query with:

```sql
SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, MemoryType,
       Manufacturer, PartNumber, SerialNumber, BankLabel, DeviceLocator,
       FormFactor, DataWidth, TotalWidth, ConfiguredVoltage, MinVoltage,
       MaxVoltage, MemoryTypeDetail, InterleavePosition, Tag
FROM Win32_PhysicalMemory
```

### Compatibility Notes

`HardwareCollector` and `CpuDetailCollector` currently construct/consume `MemoryModuleSnapshot` by positional properties only through the record shape. After extending the record, update `ToMemoryModule()` only; consumers using property names should continue to work.

If future tests or synthetic snapshots instantiate `MemoryModuleSnapshot`, they will need the new arguments. No test project currently exists.

### Form Factor Mapping

Add formatter support for common SMBIOS form factors:

```text
8  DIMM
12 SODIMM
13 SRIMM
14 SMD
15 SSMP
16 QFP
17 TQFP
18 SOIC
19 LCC
20 PLCC
21 BGA
22 FPBGA
23 LGA
```

For the page, use practical names:

- `DIMM`
- `SO-DIMM`
- `On-board`
- `Unknown`

The exact mapping can live in `MemoryTypeFormatter`.

## Milestone 2: Core Memory Detail Models

### Files To Add

```text
src/HwScope.Core/Hardware/Memory/MemoryDataSource.cs
src/HwScope.Core/Hardware/Memory/MemoryFieldValue.cs
src/HwScope.Core/Hardware/Memory/MemoryDetailReport.cs
src/HwScope.Core/Hardware/Memory/MemoryTypeFormatter.cs
```

### MemoryDataSource

```csharp
public enum MemoryDataSource
{
    Unknown,
    Wmi,
    Smbios,
    Spd,
    MemoryController,
    Computed,
    Mapping,
    Placeholder
}
```

Use source labels:

```text
WMI
SMBIOS
SPD
控制器
推导
映射
待接入
```

### MemoryFieldValue

Use a CPU-like field wrapper:

```csharp
public sealed record MemoryFieldValue<T>(
    T? Value,
    string DisplayText,
    MemoryDataSource Source,
    bool IsAvailable,
    bool IsEstimated = false,
    string? Note = null);
```

Helper class:

```csharp
public static class MemoryField
{
    public const string UnknownText = "未识别";
    public const string PendingSpdText = "待接入 SPD 读取";
    public const string PendingControllerText = "待接入内存控制器读取";

    public static MemoryFieldValue<string> Text(string? value, MemoryDataSource source, string unavailable = UnknownText, bool isEstimated = false, string? note = null);
    public static MemoryFieldValue<int> Number(int? value, MemoryDataSource source, string unavailable = UnknownText, bool isEstimated = false, string? note = null);
    public static MemoryFieldValue<ulong> Bytes(ulong value, MemoryDataSource source, bool isEstimated = false, string? note = null);
    public static MemoryFieldValue<uint> MegaTransfers(uint value, MemoryDataSource source, bool isEstimated = false, string? note = null);
    public static MemoryFieldValue<uint> MegaHertz(uint value, MemoryDataSource source, bool isEstimated = false, string? note = null);
    public static MemoryFieldValue<uint> Millivolts(uint value, MemoryDataSource source, string unavailable = UnknownText);
    public static MemoryFieldValue<T> Placeholder<T>(string text);
}
```

Formatting conventions:

- Capacity uses binary units: `32 GB`, `64 GB`.
- Configured rate uses transfer language: `5600 MT/s`.
- Internal frequency when known uses `MHz`.
- Voltage WMI values are millivolts; display as `1.10 V`.

### Report Shape

```csharp
public sealed record MemoryDetailReport(
    MemorySummary Summary,
    MemoryRuntimeInfo Runtime,
    IReadOnlyList<MemoryModuleDetail> Modules,
    IReadOnlyList<MemoryDataNote> Notes,
    DateTimeOffset GeneratedAt);
```

Summary:

```csharp
public sealed record MemorySummary(
    MemoryFieldValue<string> Type,
    MemoryFieldValue<string> TotalCapacity,
    MemoryFieldValue<int> ModuleCount,
    MemoryFieldValue<string> Layout,
    MemoryFieldValue<string> ConfiguredSpeed,
    MemoryFieldValue<string> ChannelMode);
```

Runtime:

```csharp
public sealed record MemoryRuntimeInfo(
    MemoryFieldValue<double> ClockMHz,
    MemoryFieldValue<string> EffectiveRate,
    MemoryFieldValue<string> Ratio,
    MemoryTimingValues PrimaryTimings);

public sealed record MemoryTimingValues(
    MemoryFieldValue<string> CasLatency,
    MemoryFieldValue<string> Trcd,
    MemoryFieldValue<string> Trp,
    MemoryFieldValue<string> Tras,
    MemoryFieldValue<string> Trc,
    MemoryFieldValue<string> CommandRate);
```

Module detail:

```csharp
public sealed record MemoryModuleDetail(
    string Id,
    MemoryModuleIdentity Identity,
    MemoryModuleOrganization Organization,
    MemoryModuleVoltages Voltages,
    IReadOnlyList<MemoryTimingProfile> TimingProfiles,
    IReadOnlyList<MemoryModuleFeature> Features,
    IReadOnlyList<MemoryDataNote> Notes);
```

Identity:

```csharp
public sealed record MemoryModuleIdentity(
    MemoryFieldValue<string> Slot,
    MemoryFieldValue<string> DisplayName,
    MemoryFieldValue<string> Capacity,
    MemoryFieldValue<string> ModuleType,
    MemoryFieldValue<string> MemoryType,
    MemoryFieldValue<string> MaxBandwidth,
    MemoryFieldValue<string> Manufacturer,
    MemoryFieldValue<string> DramManufacturer,
    MemoryFieldValue<string> PartNumber,
    MemoryFieldValue<string> SerialNumber,
    MemoryFieldValue<string> ManufacturingDate,
    MemoryFieldValue<string> Revision);
```

Organization:

```csharp
public sealed record MemoryModuleOrganization(
    MemoryFieldValue<string> RankMix,
    MemoryFieldValue<int> RankCount,
    MemoryFieldValue<int> BankGroupCount,
    MemoryFieldValue<int> BanksPerGroup,
    MemoryFieldValue<int> RowAddressBits,
    MemoryFieldValue<int> ColumnAddressBits,
    MemoryFieldValue<string> DeviceWidth,
    MemoryFieldValue<string> BusWidth,
    MemoryFieldValue<string> DataWidth,
    MemoryFieldValue<string> TotalWidth,
    MemoryFieldValue<string> Ecc,
    MemoryFieldValue<string> OnDieEcc);
```

Voltages:

```csharp
public sealed record MemoryModuleVoltages(
    MemoryFieldValue<string> ConfiguredVoltage,
    MemoryFieldValue<string> MinVoltage,
    MemoryFieldValue<string> MaxVoltage,
    MemoryFieldValue<string> Vdd,
    MemoryFieldValue<string> Vddq,
    MemoryFieldValue<string> Vpp);
```

Profiles:

```csharp
public sealed record MemoryTimingProfile(
    string Name,
    MemoryFieldValue<string> Frequency,
    MemoryFieldValue<string> EffectiveRate,
    MemoryFieldValue<string> CasLatency,
    MemoryFieldValue<string> Trcd,
    MemoryFieldValue<string> Trp,
    MemoryFieldValue<string> Tras,
    MemoryFieldValue<string> Trc,
    MemoryFieldValue<string> Voltage,
    MemoryDataSource Source);
```

Features:

```csharp
public sealed record MemoryModuleFeature(
    string Name,
    MemoryFieldValue<string> Value,
    MemoryDataSource Source);

public sealed record MemoryDataNote(string Message, MemoryDataSource Source);
```

### Why Not One Giant Flat Model

The page needs both CPU-Z-like quick scanning and HWiNFO-like depth. Separate records keep the UI easy to render:

- summary rows,
- runtime rows,
- selected module identity rows,
- organization rows,
- voltage rows,
- timing profiles.

## Milestone 3: MemoryDetailCollector

### Files To Add

```text
src/HwScope.Core/Hardware/Memory/MemoryDetailCollector.cs
```

### Public API

```csharp
public sealed class MemoryDetailCollector
{
    public MemoryDetailReport Collect();
    public MemoryDetailReport CreateReport(HardwareInventorySnapshot snapshot);
}
```

Like `CpuDetailCollector`, `Collect()` should call `new HardwareInventoryCollector().Collect()` for CLI/fallback compatibility. GUI should use `CreateReport(snapshot)`.

### Build Flow

```text
CreateReport(snapshot)
  modules = snapshot.MemoryModules

  Build Summary
    total capacity
    type
    module count
    layout, e.g. 2 x 32 GB
    configured speed
    channel mode placeholder

  Build Runtime
    configured/effective rate if known
    current clock placeholder
    primary timings placeholder

  Build Modules
    identity from WMI/SMBIOS fields
    organization from data/total width + placeholders
    voltages from WMI millivolts + SPD placeholders
    timing profile placeholder
    features placeholder

  Build Notes
    WMI/SMBIOS note
    SPD pending note
    memory controller pending note
    mismatch diagnostics
    inventory memory step diagnostics
```

### Stable Module IDs

Generate IDs in this order:

```text
device locator + bank label + serial + part number
tag
slot-index-N
```

Normalize to a display-safe ID, but keep original values in fields.

### Summary Calculations

Total capacity:

```csharp
modules.Sum(module => module.Capacity)
```

Layout:

```text
2 x 32 GB
```

If mixed:

```text
32 GB + 16 GB
```

Configured speed:

Prefer `ConfiguredClockSpeed`, fallback `Speed`.

If mixed speeds:

```text
5600 / 5200 MT/s
```

Type:

Prefer most common `SMBIOSMemoryType`, fallback `MemoryType`.

### Diagnostics

Add notes when:

- no memory modules returned;
- memory collection step failed or empty;
- modules have mixed capacity;
- modules have mixed configured speed;
- modules have mixed memory type;
- modules have mixed manufacturer or part number;
- SPD provider is not available yet;
- current timing provider is not available yet.

Examples:

```text
内存模块信息来自 Windows Win32_PhysicalMemory / SMBIOS，字段质量取决于主板固件。
JEDEC/XMP/EXPO 时序需要后续 SPD 读取器；当前版本不会伪造这些字段。
当前频率和 CL/tRCD/tRP/tRAS 需要后续内存控制器读取器。
检测到不同 Part Number 的内存模块，请确认是否为有意混插。
```

## Milestone 4: Formatter

### Files To Add

```text
src/HwScope.Core/Hardware/Memory/MemoryDetailReportFormatter.cs
```

Responsibilities:

- Stable text report for copy/save.
- Include all modules.
- Include sources in brackets.
- Include notes.
- Keep deterministic ordering.

Report shape:

```text
Memory / SPD

Summary
类型：DDR5 [WMI]
总容量：64 GB [WMI]
模块数：2 [WMI]
布局：2 x 32 GB [推导]
配置速率：5600 MT/s [WMI]
通道模式：待接入内存控制器读取 [待接入]

Runtime
当前内存频率：待接入内存控制器读取 [待接入]
CAS Latency：待接入内存控制器读取 [待接入]

Module 1
插槽：Channel A / DIMM 0 [WMI]
容量：32 GB [WMI]
...

Timing Profiles
  待接入 SPD 读取

Notes
  - ...

Generated At: yyyy-MM-dd HH:mm:ss
```

## Milestone 5: MemoryDetailPage UI

### Files To Add

```text
src/HwScope.App/Pages/MemoryDetailPage.xaml
src/HwScope.App/Pages/MemoryDetailPage.xaml.cs
```

### Page Responsibilities

- Subscribe to `App.HardwarePreload.InventoryChanged`.
- Initial load from `EnsureLoadedAsync`.
- Refresh through `RefreshAsync`.
- Preserve selected module when possible.
- Render summary/runtime/module/timing/notes.
- Copy/save through `MemoryDetailReportFormatter`.
- Raise `StatusChanged` for MainWindow footer.

### Async Pattern

Use the same version guard as CPU page:

```csharp
private int _refreshVersion;

private async Task RefreshAsync(bool forceRefresh)
{
    var version = Interlocked.Increment(ref _refreshVersion);
    SetBusy(true);
    try
    {
        var snapshot = forceRefresh
            ? await App.HardwarePreload.RefreshAsync().ConfigureAwait(true)
            : await App.HardwarePreload.EnsureLoadedAsync().ConfigureAwait(true);
        var report = _reportBuilder.CreateReport(snapshot);
        if (version != _refreshVersion)
        {
            return;
        }

        Render(report);
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

### XAML Layout

Use the CPU page as a close visual sibling.

```text
Grid
  Row 0: Header
  Row 1: ScrollViewer
    StackPanel
      Header summary card
      Runtime overview section
      Module selector section
      WrapPanel detail sections
      Timing profile section
      Notes section
```

Recommended named controls:

```text
MemorySubtitleText
SummaryChipsList
RuntimeSectionList
ModuleTilesList
SelectedModuleTitleText
SelectedModuleMetaText
ModuleSectionList
TimingProfilesList
NotesList
RefreshButton
CopyButton
SaveButton
```

### View Records

In `MemoryDetailPage.xaml.cs`:

```csharp
public sealed record MemorySectionView(string Title, IReadOnlyList<MemoryFieldRowView> Rows);
public sealed record MemoryFieldRowView(string Label, string Value, string Source, string SourceDescription);
public sealed record MemoryModuleTileView(string Id, string Title, string Subtitle, string Detail, bool IsSelected, bool HasWarning);
public sealed record MemoryTimingProfileView(...);
```

If WPF binding to records becomes cumbersome for selected tile styling, use simple classes with settable properties.

### Module Tile Interaction

Use a Button or Border-with-mouse-click item template:

- button content is the tile;
- command/click sets `_selectedModuleId`;
- re-render selected module sections.

Keep fixed dimensions:

```text
Width 290
MinHeight 92
```

### Timing Profiles

First implementation can render:

- if no real profiles: one quiet row/card saying `待接入 SPD 读取`;
- later: table rows.

Do not fake JEDEC values from configured speed.

### Source Badge

Use the same label style as CPU:

```text
WMI
SMBIOS
SPD
控制器
推导
映射
待接入
```

Estimated values append `*`.

## Milestone 6: Navigation Integration

### MainWindow.xaml

Change memory navigation item:

```xml
<ui:NavigationViewItem Content="内存"
                       Tag="memory-detail"
                       Icon="{ui:SymbolIcon Database24}"
                       Click="NavigationItem_Click" />
```

Top toolbar `内存` currently routes to summary. Update it to open memory detail if this change introduces the dedicated page:

```xml
<Button ToolTip="内存"
        Click="ShowMemoryDetail_Click">
```

### MainWindow.xaml.cs

Add field:

```csharp
private readonly MemoryDetailPage _memoryDetailPage = new();
```

Wire status:

```csharp
_memoryDetailPage.StatusChanged += (_, status) => SetFooterStatus(status);
```

Route:

```csharp
case "memory-detail":
    ShowMemoryDetail();
    break;
```

Method:

```csharp
private void ShowMemoryDetail()
{
    PageHost.Content = _memoryDetailPage;
    SetFooterStatus("内存 / SPD 详情。");
}
```

## Milestone 7: Documentation Updates

### Files To Modify

```text
README.md
docs/project-architecture.md
docs/hardware-preload-design.md
```

Update README:

- Add Memory/SPD detail page to feature list.
- Add navigation note.
- Add limitation: SPD raw profiles pending until native SPD reader.

Update architecture:

- Add `HwScope.Core.Hardware.Memory`.
- Add `Pages/MemoryDetailPage`.
- Mention inventory enrichment.

Update preload design:

- Add enriched memory module fields.
- Mention MemoryDetailPage consumes preload service.

## Milestone 8: Build And Manual Validation

### Build

```powershell
dotnet build
```

### GUI Manual Validation

Run:

```powershell
dotnet run --project .\src\HwScope.App\HwScope.App.csproj
```

Checklist:

- Startup preload still succeeds.
- `硬件 -> 内存` opens Memory / SPD page.
- Top toolbar `内存` opens Memory / SPD page.
- Page shows total capacity, type, layout, configured speed.
- Module tiles appear for each `Win32_PhysicalMemory` item.
- Selecting module changes details.
- Refresh disables controls and updates from shared inventory.
- Copy places a readable report on clipboard.
- Save writes a `.txt` report.
- Missing SPD and runtime timing fields show explicit pending labels.
- Light/dark themes remain readable.
- Resize near minimum width: no overlapping rows/buttons.

### Regression Checks

- Summary page still shows memory summary.
- CPU page still shows platform memory type/clock.
- Memory benchmark window still opens and receives header.
- CLI summary still builds/runs.

Optional:

```powershell
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- --json
```

## Stage 1 Definition Of Done

Stage 1 is complete when:

- Inventory reads extended `Win32_PhysicalMemory` fields.
- Core memory detail records, collector, formatter exist.
- Memory detail page exists and is reachable from navigation.
- Page renders from `HardwarePreloadService`, not direct WMI.
- Page shows summary, runtime placeholders, module selector, selected module details, timing placeholder, notes.
- Copy/save work.
- `dotnet build` succeeds.
- README/architecture docs mention the page and current SPD limitations.

## Stage 2 Definition Of Done

Stage 2 is complete when:

- Page uses enriched WMI fields for manufacturer, part number, serial, locator, form factor, width and voltages.
- Mismatch diagnostics are visible.
- Field source labels distinguish WMI, computed and placeholder values.

Stage 2 is intentionally close to Stage 1. It may be delivered in the same implementation if scope remains manageable.

## Stage 3 Native SPD Reader Plan

The first UI change now has the Core boundary in place. The native worker project itself is still a later deliverable, but the app can already consume a schema-versioned `spd.exe --json` worker when present and degrade to structured notes when it is missing.

### Project Shape

```text
src/HwScope.Native.Spd/
  CMakeLists.txt
  README.md
  scripts/build-msvc.ps1
  src/main.cpp
```

### CLI Contract

```powershell
spd.exe --json
```

Output:

```json
{
  "schemaVersion": 1,
  "status": "ok",
  "modules": [
    {
      "locator": "DIMM 0",
      "type": "DDR5",
      "moduleType": "SO-DIMM",
      "capacityBytes": 34359738368,
      "manufacturer": "Micron Technology",
      "dramManufacturer": "Micron",
      "partNumber": "CT32G56C46S5.M16D1",
      "serialNumber": "EB235139",
      "manufacturingWeek": 4,
      "manufacturingYear": 2025,
      "timingProfiles": [
        {
          "name": "JEDEC #9",
          "frequencyMHz": 2800,
          "casLatency": 46,
          "trcd": 45,
          "trp": 45,
          "tras": 90,
          "trc": 135,
          "voltageMv": 1100
        }
      ]
    }
  ],
  "diagnostics": []
}
```

Failure example:

```json
{
  "schemaVersion": 1,
  "status": "platformBlocked",
  "modules": [],
  "diagnostics": [
    "SMBus/SPD access is blocked by firmware or unavailable on this platform."
  ]
}
```

### Core Provider

```text
src/HwScope.Core/Hardware/Memory/ISpdProvider.cs
src/HwScope.Core/Hardware/Memory/NativeSpdProcessProvider.cs
src/HwScope.Core/Hardware/Memory/SpdProviderResult.cs
src/HwScope.Core/Hardware/Memory/NullSpdProvider.cs
```

API:

```csharp
public interface ISpdProvider
{
    SpdProviderResult TryCollect();
}
```

Missing worker is expected during development and should produce notes, not exceptions through the page.

Implemented behavior:

- `MemoryDetailCollector` depends on `ISpdProvider` and uses `NativeSpdProcessProvider` by default.
- `NativeSpdProcessProvider` searches only relative worker locations under the app/base build tree.
- Worker status values are mapped into `SpdProviderStatus` and shown through report notes and the summary chip.
- SPD module data, when available, can override module identity fields and populate timing profiles.
- Missing worker, timeout, parse failure, permission failure and platform-blocked states are non-fatal.

## Risks And Mitigations

### Existing Summary Semantics

Risk: extending `MemoryModuleSnapshot` changes constructor signatures and may accidentally break summary or CPU platform context.

Mitigation:

- Update only the collector constructor call.
- Keep existing properties unchanged.
- Run `dotnet build`.

### WMI Optional Fields

Risk: Some WMI fields are absent or zero on some systems.

Mitigation:

- `Wmi.GetString/GetUInt` already handles missing values.
- Format zero voltage/width as unavailable.
- Add notes instead of failing page load.

### Page Complexity

Risk: implementing full HWiNFO density in one pass makes the page cluttered.

Mitigation:

- First pass renders key WMI fields and placeholders.
- Keep advanced SPD-only fields grouped and clearly marked pending.

### Misleading Runtime Values

Risk: Users may read configured speed as current frequency.

Mitigation:

- Label as `配置速率` / `Configured Speed`.
- Keep `当前内存频率` pending until a real provider exists.

### SPD Scope Creep

Risk: Raw SPD access is platform-sensitive and could derail the page.

Mitigation:

- Ship WMI-backed page first.
- Treat SPD provider as a future optional data source.
