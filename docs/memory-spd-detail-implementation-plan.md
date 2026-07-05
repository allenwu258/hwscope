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

The first UI change now has the Core boundary in place. The native worker project scaffold is available and the app can consume a schema-versioned `spd.exe --json` worker when present. The scaffold returns a structured non-fatal status until raw SMBus/SPD access is implemented.

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
- `src/HwScope.Native.Spd` builds a minimal `spd.exe --json` worker that returns `notImplemented` with diagnostics rather than fake module data.
- Timing fields such as `casLatency`, `trcd`, `trp`, `tras`, and `trc` may be JSON numbers or strings.
- Worker status values are mapped into `SpdProviderStatus` and shown through report notes and the summary chip.
- SPD module data, when matched by serial, part number + locator, locator, or the single-module case, can override module identity fields and populate timing profiles.
- Missing worker, timeout, parse failure, permission failure and platform-blocked states are non-fatal.

## Stage 3A/3B Real SPD Reading And Parsing Plan

真实 SPD 支持要拆成两条相互解耦的工程线：

- **SPD bytes parser**：给定一段 raw SPD bytes，稳定解析 DDR4/DDR5 字段、校验和、module identity、organization、voltages 和 timing profiles。这个部分必须能用 fixture 离线测试，不依赖当前机器能否读取 SMBus。
- **Raw SPD reader**：在 Windows 上尽力枚举 SMBus/SPD EEPROM 并读取 bytes。这个部分是平台敏感、权限敏感和硬件敏感的，应作为可替换 backend，并且失败不影响 WMI/SMBIOS 页面。

第一批真实开发不要直接把 SMBus 读取和 DDR 解析写在一起。先让 `spd.exe` 能通过 fixture/导入 bytes 输出真实解析结果，再逐步接入 Windows raw reader。这样可以在没有可读 SPD 的笔记本、LPDDR/onboard memory、OEM BIOS 屏蔽平台上继续推进解析和 UI 合并。

### Goals

- `spd.exe --json` 能输出 schema-versioned raw access status、模块列表、diagnostics 和解析后的 SPD 字段。
- 支持 fixture/offline 输入，便于开发、测试、回归和第三方样本排查。
- 先支持 DDR5 UDIMM/SO-DIMM 和 DDR4 UDIMM/SO-DIMM 的关键字段；RDIMM/LRDIMM、LPDDR、CAMM 和厂商私有扩展作为后续增强。
- Raw reader 失败必须结构化：权限不足、平台屏蔽、未实现、unsupported memory type、checksum failed、parse failed。
- 页面始终保留 WMI/SMBIOS-backed fallback，SPD 只覆盖明确匹配且校验可信的字段。

### Non-Goals For First Real Reader

- 不内置未签名 kernel driver。
- 不承诺读取所有芯片组、所有 OEM BIOS 或 LPDDR/onboard memory。
- 不把 SPD 静态 profile 当作当前运行态时序。
- 不解析所有厂商私有 bytes。
- 不在 UI 上显示未验证的 raw bytes 为事实字段。

### Native Worker Architecture

`src/HwScope.Native.Spd/src/main.cpp` 应拆分为小型模块，避免单文件膨胀：

```text
src/HwScope.Native.Spd/
  src/
    main.cpp
    cli_options.h/.cpp
    json_writer.h/.cpp
    spd_result.h
    spd_bytes.h
    spd_checksum.h/.cpp
    spd_parser.h/.cpp
    spd_parser_ddr4.h/.cpp
    spd_parser_ddr5.h/.cpp
    spd_reader.h
    spd_reader_fixture.h/.cpp
    spd_reader_windows_smbus.h/.cpp
```

Core types:

```cpp
enum class SpdStatus {
    Ok,
    NotImplemented,
    WorkerMissing,
    AccessDenied,
    PlatformBlocked,
    UnsupportedMemoryType,
    ChecksumFailed,
    ParseFailed,
    Timeout,
    Failed
};

struct RawSpdImage {
    std::string locator;
    std::string source;
    std::vector<std::uint8_t> bytes;
    bool checksum_ok;
    std::vector<std::string> diagnostics;
};

struct ParsedSpdModule {
    std::string locator;
    std::string type;
    std::string module_type;
    std::uint64_t capacity_bytes;
    std::string manufacturer;
    std::string dram_manufacturer;
    std::string part_number;
    std::string serial_number;
    int manufacturing_week;
    int manufacturing_year;
    std::string revision;
    std::vector<ParsedTimingProfile> timing_profiles;
    std::vector<ParsedFeature> features;
    std::vector<std::string> diagnostics;
};
```

### CLI Contract Evolution

Keep the current base command:

```powershell
spd.exe --json
```

Add development and diagnostics options:

```powershell
spd.exe --json --backend auto
spd.exe --json --backend fixture --fixture .\fixtures\ddr5-so-dimm-32gb.json
spd.exe --json --dump-raw
spd.exe --json --probe-only
```

Backends:

| Backend | Purpose | Expected Status |
| --- | --- | --- |
| `auto` | default production path, tries safe native readers | `ok`, `platformBlocked`, `accessDenied`, `unsupportedMemoryType`, `notImplemented` |
| `fixture` | parser development and regression tests | `ok`, `parseFailed`, `checksumFailed` |
| `windows-smbus` | Windows raw SMBus/SPD reader | `ok`, `platformBlocked`, `accessDenied`, `failed` |

Initial schema can remain `schemaVersion: 1` if new fields are optional. Bump schema only when existing field meaning changes.

Recommended output shape:

```json
{
  "schemaVersion": 1,
  "workerVersion": "0.2.0",
  "status": "ok",
  "backend": "fixture",
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
      "revision": "1.0",
      "organization": {
        "rankCount": 2,
        "deviceWidthBits": 16,
        "busWidthBits": 64,
        "dieCount": null,
        "bankGroups": 8,
        "banksPerGroup": 4
      },
      "voltages": {
        "vddMv": 1100,
        "vddqMv": 1100,
        "vppMv": 1800
      },
      "timingProfiles": [
        {
          "name": "JEDEC #9",
          "kind": "jedec",
          "frequencyMHz": 2800,
          "effectiveRateMTps": 5600,
          "casLatency": 46,
          "trcd": 45,
          "trp": 45,
          "tras": 90,
          "trc": 135,
          "voltageMv": 1100
        }
      ],
      "raw": {
        "byteCount": 512,
        "checksumOk": true,
        "crcOk": true,
        "sha256": "..."
      },
      "diagnostics": []
    }
  ],
  "diagnostics": []
}
```

`raw.bytes` should not be emitted by default. If `--dump-raw` is passed, include hex/base64 raw bytes for diagnostics and bug reports. The GUI should not display raw dumps in the normal page.

### Parser Implementation Plan

Parser should be byte-array first:

```cpp
ParseResult parse_spd(std::span<const std::uint8_t> bytes);
```

Detection:

- Validate minimum length before reading offsets.
- Detect memory technology from SPD header bytes.
- Route to DDR4 or DDR5 parser.
- Unknown or LPDDR types return `unsupportedMemoryType` with diagnostics, not crash.

Validation:

- Validate checksum/CRC blocks applicable to the detected SPD generation.
- Parser may return partial fields with `checksumOk=false`, but final worker status should be `checksumFailed` if no trustworthy module can be produced.
- Never use bytes outside validated bounds.

DDR4 first-pass fields:

- SPD revision.
- DRAM device type.
- Module type.
- Module nominal voltage.
- SDRAM density / bank addressing / package type.
- Module organization: rank count, device width, bus width.
- Module manufacturer ID and module manufacturing location/date.
- Serial number and part number.
- JEDEC timing profiles sufficient for table display.

DDR5 first-pass fields:

- SPD revision.
- Base module type.
- SDRAM density / IO width / bank groups / banks.
- Module organization: ranks, data width, bus width.
- Module nominal voltages: VDD, VDDQ, VPP where encoded.
- Module manufacturer, DRAM manufacturer where available, serial, part number, manufacturing date.
- JEDEC timing profiles sufficient for table display.
- DDR5 feature flags that are already present in UI placeholders: write temperature sense, bounded fault, BL32, non-standard timings.

XMP / EXPO:

- Do not make XMP/EXPO part of the first parser merge unless the base JEDEC parser is stable.
- Add extension parser hooks:

```cpp
std::vector<ParsedTimingProfile> parse_xmp_profiles(...);
std::vector<ParsedTimingProfile> parse_expo_profiles(...);
```

- Profiles must be labeled `kind: "xmp"` or `kind: "expo"` and must include diagnostics if partially parsed.

Manufacturer ID decoding:

- Implement a local JEDEC manufacturer table file or generated header only after deciding the data source and license constraints.
- Until then, return hex manufacturer IDs and let UI show `未识别` or `ID xxxx` rather than guessing names.

### Raw Reader Implementation Plan

Windows does not expose a stable universal user-mode API for raw SPD EEPROM reads. Treat raw access as optional backend work.

Reader interface:

```cpp
class ISpdReader {
public:
    virtual ~ISpdReader() = default;
    virtual ReaderResult collect() = 0;
};
```

Reader result:

```cpp
struct ReaderResult {
    SpdStatus status;
    std::string backend;
    std::vector<RawSpdImage> images;
    std::vector<std::string> diagnostics;
};
```

Backends by phase:

1. **Fixture reader**
   - Reads JSON fixture files containing locator, source and hex/base64 SPD bytes.
   - Used for unit/regression tests and parser development.
   - This is the first backend to implement.

2. **Windows probe reader**
   - Enumerates candidate SMBus controllers and reports diagnostics only.
   - Does not read hardware yet.
   - Helps collect chipset/vendor/device IDs and platform-blocked cases.

3. **Windows SMBus reader**
   - Attempts raw reads only when a supported controller path exists.
   - Must clearly report when admin/driver/platform access is missing.
   - Must guard every IO operation; failures return structured diagnostics.

4. **Optional privileged backend**
   - If a driver is required, it must be explicit, signed for release, and disabled by default during development.
   - Do not silently install or load third-party drivers.

SMBus reader acceptance rules:

- No bluescreen-prone direct IO in ordinary GUI process.
- No hidden elevation prompt from the worker.
- No raw reads unless backend can identify a supported controller.
- No infinite retries; worker timeout remains bounded by Core.
- LPDDR/onboard memory without SPD EEPROM should return `unsupportedMemoryType` or `platformBlocked`.

### Fixture Format

Store fixtures under:

```text
src/HwScope.Native.Spd/fixtures/
  ddr4-udimm-8gb.sample.json
  ddr5-sodimm-32gb.sample.json
```

Fixture JSON:

```json
{
  "locator": "DIMM 0",
  "source": "fixture",
  "memoryType": "DDR5",
  "bytesHex": "2310...",
  "expected": {
    "type": "DDR5",
    "moduleType": "SO-DIMM",
    "capacityBytes": 34359738368,
    "partNumber": "CT32G56C46S5.M16D1"
  }
}
```

Do not commit user-private raw SPD dumps without review. SPD dumps can contain serial numbers and manufacturing details. Public fixtures should be synthetic, vendor-sanitized, or explicitly approved.

### Core Integration Plan

Current Core result types should be extended conservatively:

```text
SpdMemoryModule
  Organization
  Voltages
  Raw
  Diagnostics

SpdTimingProfile
  Kind
  EffectiveRateMTps
```

`NativeSpdProcessProvider` should:

- Parse optional new fields without requiring them.
- Keep accepting current minimal scaffold output.
- Map `notImplemented` separately from `platformBlocked`.
- Treat unknown fields as forward-compatible.
- Avoid throwing through `MemoryDetailPage`; convert parse errors to `ParseFailed`.

`MemoryDetailCollector` should:

- Only override WMI fields when SPD module matching is strong.
- Keep WMI slot/locator as the display anchor unless SPD locator matches.
- Add module-level diagnostics for checksum failure, partial parse or weak matching.
- Fill organization and voltage fields from SPD only when available.
- Replace pending timing row with real JEDEC/XMP/EXPO rows when parsed.

Matching priority remains:

1. Serial number.
2. Part number + locator.
3. Locator.
4. Single WMI module + single SPD module.

No multi-module index fallback.

### UI Integration Plan

When real SPD data is present:

- Summary chip: `SPD 已读取`.
- Timing Profiles table:
  - `Kind` may be shown in the profile name: `JEDEC #9`, `XMP #1`, `EXPO #1`.
  - Source badge remains `SPD`.
- Module identity:
  - DRAM manufacturer, production date and revision should move from placeholder to SPD.
- Module organization:
  - Rank count, device width, bus width, bank group/bank counts should move from placeholder to SPD.
- Voltages/features:
  - VDD/VDDQ/VPP and DDR5 feature bits should move from placeholder to SPD.
- Notes:
  - Show checksum/CRC status.
  - Show backend diagnostics only in data notes, not as blocking errors.

If SPD status is `notImplemented`, `workerMissing`, `platformBlocked`, `accessDenied`, `unsupportedMemoryType`, `checksumFailed` or `parseFailed`, the page remains usable with WMI-backed fields.

### Testing And Verification

Native tests:

- `spd.exe --json --backend fixture --fixture fixtures\ddr5-sodimm-32gb.sample.json`
- `spd.exe --json --backend fixture --fixture fixtures\ddr4-udimm-8gb.sample.json`
- Invalid fixture path returns non-zero with clear stderr.
- Bad checksum fixture returns `checksumFailed`.
- Unknown DDR type returns `unsupportedMemoryType`.

Core tests or probe commands:

- Provider parses numeric and string timing fields.
- Provider parses optional organization/voltage/raw sections.
- Provider maps `notImplemented`, `platformBlocked`, `accessDenied`, `checksumFailed` and `parseFailed`.
- Collector keeps WMI fallback when SPD has no modules.
- Collector does not cross-assign SPD modules in multi-module systems.

Manual hardware matrix:

| Platform | Expected |
| --- | --- |
| DDR5 SO-DIMM laptop with readable SPD | `ok`, real JEDEC profiles |
| DDR4 desktop DIMM | `ok`, DDR4 identity and JEDEC profiles |
| LPDDR/onboard laptop | `unsupportedMemoryType` or `platformBlocked` |
| OEM BIOS blocks SMBus | `platformBlocked` |
| Non-admin where backend requires privilege | `accessDenied` |
| No worker built | `workerMissing` |

Required validation before merging a real reader:

```powershell
.\src\HwScope.Native.Spd\scripts\build-msvc.ps1
.\src\HwScope.Native.Spd\build\Release\spd.exe --json
dotnet build
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- --json
```

If fixture backend exists:

```powershell
.\src\HwScope.Native.Spd\build\Release\spd.exe --json --backend fixture --fixture .\src\HwScope.Native.Spd\fixtures\ddr5-sodimm-32gb.sample.json
```

### Implementation Milestones

1. **Parser scaffold**
   - Split native worker files.
   - Add CLI options and JSON writer.
   - Add fixture backend and raw bytes model.
   - Keep current `notImplemented` default for `--backend auto`.

2. **Checksum and detection**
   - Implement safe byte access helpers.
   - Detect DDR4/DDR5/unsupported.
   - Validate checksum/CRC enough to gate trust.

3. **DDR5 parser first pass**
   - Parse identity, module type, capacity, organization, voltages and JEDEC timing profiles.
   - Add synthetic/sanitized DDR5 fixture.

4. **DDR4 parser first pass**
   - Parse identity, module type, capacity, organization, voltages and JEDEC timing profiles.
   - Add synthetic/sanitized DDR4 fixture.

5. **Core JSON expansion**
   - Extend `SpdMemoryModule`, parser and collector.
   - Fill UI placeholders from fixture-backed SPD data.

6. **Windows probe backend**
   - Enumerate candidate controllers where possible.
   - Report diagnostics and platform restrictions without reading hardware.

7. **Windows SMBus backend**
   - Implement one supported controller family behind explicit capability checks.
   - Keep failure structured and bounded.

8. **XMP/EXPO and advanced DDR5**
   - Add extension parsers after JEDEC base parser stabilizes.
   - Add UI row grouping if profile count grows.

### Stage 3 Definition Of Done

Stage 3 parser is done when:

- Fixture backend can parse at least one DDR4 and one DDR5 sample.
- Core can render real SPD identity, organization, voltage and JEDEC timing rows from fixture data.
- Bad checksum, unsupported type and malformed bytes are covered by tests/probes.
- WMI fallback remains unchanged when SPD fails.

Stage 3 raw reader is done when:

- `spd.exe --json --backend auto` can return `ok` with real SPD bytes on at least one supported Windows DDR4/DDR5 platform.
- Platform blocked and access denied cases are distinguishable.
- Worker cannot hang the GUI beyond Core timeout.
- No privileged/driver behavior happens implicitly.

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
