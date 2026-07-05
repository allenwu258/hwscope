# Memory / SPD Detail Page Requirements And UI Design

本文档定义 HwScope 真正的内存 / SPD 详情页需求分析、信息架构和 UI 设计。参考对象是 CPU-Z 的 `内存` / `SPD` 页、AIDA64 的 `SPD` 详情页和 HWiNFO 的内存模块详情视图。目标不是复刻旧式控件皮肤，而是吸收三者的信息组织方式，做成符合 HwScope Fluent/WPF shell 的高密度诊断页面。

## Goals

内存 / SPD 详情页要解决四个问题：

- 让用户一眼确认当前系统内存总量、类型、通道模式、运行频率和主要时序。
- 让用户能按插槽查看每根内存条的 SPD 信息，包括容量、厂商、料号、序列号、生产日期、电压、rank / bank / bus width 和 JEDEC / XMP / EXPO 时序表。
- 给进阶用户提供接近 CPU-Z / AIDA64 / HWiNFO 的可验证字段，方便截图、排查混插、核对频率/时序、确认内存颗粒和模块组织。
- 为后续内存跑分、稳定性测试、报告导出和故障诊断建立结构化内存模型。

当前 HwScope 的内存信息主要来自 `Win32_PhysicalMemory`，只在摘要页显示类似：

```text
64GB DDR5 5600MHz（32GB + 32GB）
```

这不够支持 SPD 级诊断。新页面需要把“运行态内存概览”和“每根模块 SPD 详情”拆成结构化模型，并明确区分 WMI/SMBIOS 可得字段、SPD 原始读取字段、推导字段和待接入字段。

## Non-Goals

第一版不做以下内容：

- 不实现完整 AIDA64/HWiNFO 硬件树，只做主窗口导航里的内存详情页。
- 不把内存跑分结果嵌入这个页面；跑分仍保留独立窗口。
- 不把无法读取的 SPD 字段伪造成事实。采不到时显示 `未识别`、`待接入 SPD 读取` 或 `不支持`。
- 不做内存稳定性测试、温度传感器、PMIC 实时遥测。
- 不在第一版承诺读取所有厂商私有 SPD 扩展字段。
- 不直接依赖截图中的某一台机器字段值；截图用于信息结构参考。

## Reference Analysis

### CPU-Z Memory Page

CPU-Z 的 `内存` 页强调运行态信息：

- 顶部 `常规` 区展示内存类型、总容量、通道数、DC 模式、北桥频率。
- 下方 `时序` 区展示当前内存频率、FSB:DRAM、CL、tRCD、tRP、tRAS、tRC、CR 等。
- 优点是非常适合“一眼确认现在跑在什么频率和时序”。
- 不足是字段解释少，不能看每根模块 SPD 细节。

HwScope 应吸收这部分作为页面顶部的 `运行态概览`，但要给来源标记：很多运行态时序不能从普通 WMI 拿到，第一版只能显示占位或来自 future native/SPD provider。

### CPU-Z SPD Page

CPU-Z 的 `SPD` 页强调单插槽：

- 有插槽选择器，例如 `插槽 #1`。
- 模块概要：DDR 类型、模块大小、最大带宽、制造商、DRAM 制造商、型号、序列号、周次/年份。
- 下方 JEDEC timing table 横向展示多个 profile：频率、CL、tRCD、tRP、tRAS、tRC、电压。
- 优点是插槽维度清晰，时序表适合对比 profile。
- 不足是字段较少，对 DDR5 的 rank/bank/PMIC/特性展示不如 HWiNFO。

HwScope 应把 CPU-Z SPD 页作为 `模块选择 + SPD timing table` 的基础形态。

### AIDA64 SPD Page

AIDA64 的 `SPD` 页面更像详细属性树：

- 顶部列出设备描述，例如 `DIMM1`、`DIMM2`。
- 详情区按组展示：
  - 内存模块：模块名称、序列号、制造日期、容量、模块类型、存取类型、速度、位宽、电压、错误检测、DRAM 制造商、die count。
  - 内存计时：多条频率 profile，每条包含 CL-RCD-RP-RAS 和更多扩展时序。
  - 模块特性：Asymmetrical Module、HeatSpreader 等。
  - 制造商：公司名、产品链接。
- 优点是字段完整、纵向可滚动，适合做“事实清单”。
- 不足是对横向 profile 对比不如 CPU-Z 表格直观。

HwScope 应吸收 AIDA64 的分组详情形式，作为所选模块右侧或下方的详细属性区。

### HWiNFO Memory Module View

HWiNFO 的内存模块详情最密集：

- 左侧硬件树按 `内存 -> 行: 0/1 [channel/dimm]` 选择模块。
- 右侧两列表格 `特征 / 描述`，包含：
  - 常规信息：编号、容量、类型、模块类型、速度、制造商、部件号、序列号、生产日期、修订、错误检测。
  - 模块特征：rank mix、地址位、bank group、rank 数、device width、module organization、通道数、总线扩展。
  - 电压：VDD、VDDQ、VPP 标称/可操作/耐久。
  - DDR5 特性：Write Temperature Sense、Bounded Fault、BL32、Non-Standard Core Timings。
  - 最小 ns 约束和 supported timings。
- 优点是字段最完整，尤其适合 DDR5。
- 不足是信息密度极高，普通用户难扫读。

HwScope 应吸收 HWiNFO 的字段覆盖范围，但用分区和表格降低认知负担。

## Product Scope

内存 / SPD 详情页分阶段交付。

### Stage 1: WMI / SMBIOS Backed Memory Detail

第一阶段目标是做出可用的内存详情页，基于当前 `HardwareInventorySnapshot.MemoryModules` 和可扩展的 `Win32_PhysicalMemory` 字段。

必须展示：

- 总容量。
- 内存类型，例如 DDR4 / DDR5 / LPDDR5。
- 模块数量。
- 每根模块容量。
- ConfiguredClockSpeed / Speed。
- SMBIOSMemoryType / MemoryType 映射结果。
- 当前可识别的插槽/位置字段：`BankLabel`、`DeviceLocator`，需要扩展 snapshot。
- Manufacturer、PartNumber、SerialNumber，需要扩展 snapshot。
- 数据来源和缺失说明。

必须支持：

- 主窗口左侧 `硬件 -> 内存` 进入内存详情页，而不是继续路由到摘要页。
- 刷新按钮触发全局 inventory refresh。
- 复制内存详情为文本。
- 保存 `.txt` 报告。
- 字段级来源标记。

Stage 1 允许以下字段显示为占位：

- 当前真实时序 CL/tRCD/tRP/tRAS/tRC。
- 通道模式、rank/bank 组织。
- JEDEC / XMP / EXPO profile。
- PMIC / 电压 / DDR5 特性。

### Stage 2: SMBIOS Enrichment And Layout Diagnosis

第二阶段在不读原始 SPD 的前提下，尽量扩展 Windows/SMBIOS 可得字段。

目标字段：

- `BankLabel`
- `DeviceLocator`
- `FormFactor`
- `Manufacturer`
- `PartNumber`
- `SerialNumber`
- `ConfiguredVoltage`、`MinVoltage`、`MaxVoltage`
- `TotalWidth`、`DataWidth`
- `InterleavePosition`
- `MemoryTypeDetail`
- `Tag`

目标能力：

- 给模块生成稳定 label：`DIMM 0 / Channel A / Slot 1`，没有真实 channel 时用 `Slot 1`。
- 识别混插：容量、速度、厂商、料号不一致时给出诊断提示。
- 从模块数量和总容量构建拓扑摘要，但明确标注是否只是 SMBIOS/WMI 视角。

### Stage 3: Raw SPD Reader

第三阶段接入原始 SPD 读取。可以采用 native worker 进程，类似内存跑分 worker 和未来 CPUID worker。

目标字段：

- SPD revision。
- Module type：UDIMM / SO-DIMM / RDIMM / LPDDR onboard 等。
- Module nominal capacity。
- Module organization：rank count、device width、bus width、bank groups、banks、row/column address bits。
- Module manufacturer、DRAM manufacturer、part number、serial number、manufacturing week/year。
- DDR5 voltages：VDD、VDDQ、VPP。
- ECC / on-die ECC / error correction capability。
- JEDEC timing profiles。
- XMP / EXPO profile。
- DDR5 feature bits：Write Temperature Sense、Bounded Fault、BL32 等。
- Raw SPD bytes and checksum status。

推荐 worker 形态：

```text
HwScope.Native.Spd/spd.exe --json
```

输出应为 schema-versioned JSON。缺少 SMBus 权限、被 BIOS/平台屏蔽、笔记本 LPDDR 无可读 SPD 等情况必须是非致命状态。

### Stage 4: Memory Controller / Live Timing Provider

第四阶段接入运行态内存控制器信息。

目标字段：

- 当前内存频率。
- 当前 CL / tRCD / tRP / tRAS / tRC / CR。
- Gear mode / command rate / memory controller ratio。
- Channel count / active channels。
- DRAM:FSB 或 memory controller ratio。
- DDR5 MCLK / UCLK / FCLK 类平台相关字段。

这可能需要 native CPUID/MSR/PCI config/厂商平台 provider。所有字段必须标注平台限制。

### Stage 5: Sensor And Stability Context

第五阶段扩展：

- DIMM 温度。
- PMIC 电压/温度。
- ECC error counters。
- 内存训练状态、MRC/AGESA 相关信息。
- 与内存跑分结果、压力测试结果关联。

## Information Architecture

页面采用“顶部运行态概览 + 模块选择 + 模块 SPD 详情”的结构。桌面宽度下三段纵向排列，局部两列；窄宽度下单列堆叠。

```text
Memory / SPD Detail Page
  Header
    Title, summary subtitle, status chips, refresh/copy/save actions

  Runtime Overview
    Memory type, total capacity, module count, active channels
    current frequency, effective transfer rate, primary timings
    source and live/static indicator

  Module Strip / Slot Selector
    compact module tiles: slot, capacity, speed, vendor, part number
    selected module highlighted

  Main Detail
    Left/Top: selected module summary
    Right/Below: timing profile table
    Sections:
      Module Identity
      Module Organization
      Voltages And Features
      Timing Profiles
      Manufacturer / Diagnostics

  Footer / Notes
    data source notes, missing fields, last refresh, SPD access status
```

## Field Groups

### Header

用途：一眼确认系统内存配置。

字段：

- Title: `内存 / SPD`
- Subtitle: `64 GB DDR5 · 2 modules · DDR5-5600`
- Status Chips:
  - `DDR5`
  - `64 GB`
  - `2 x 32 GB`
  - `5600 MT/s`
  - `SPD 待接入` or `SPD 已读取`

动作：

- Refresh
- Copy
- Save

### Runtime Overview

对应 CPU-Z `内存` 页。

字段：

| Label | Example | Source Strategy |
| --- | --- | --- |
| 类型 | DDR5 | WMI/SMBIOS, future SPD |
| 总容量 | 64 GB | WMI |
| 模块数 | 2 | WMI |
| 通道模式 | Dual Channel / 4 x 32-bit | future controller provider / computed placeholder |
| 当前内存频率 | 2791.9 MHz | future live provider |
| 有效速率 | DDR5-5600 / 5600 MT/s | WMI ConfiguredClockSpeed or SPD |
| FSB:DRAM / Ratio | 1:28 | future live provider |
| CAS Latency (CL) | 46 | future live provider / SPD selected profile |
| tRCD | 45 | future live provider |
| tRP | 45 | future live provider |
| tRAS | 90 | future live provider |
| tRC | 135 | future live provider |
| Command Rate | 1T / 2T | future live provider |

第一版应避免把 `ConfiguredClockSpeed` 当作“当前频率”。推荐显示：

```text
配置速率：5600 MT/s [WMI]
当前时序：待接入内存控制器读取
```

### Module Selector

对应 CPU-Z SPD 的插槽选择器、AIDA64/HWiNFO 的模块列表。

展示方式：

- 一行或可换行的模块 tiles。
- 每个 tile 固定宽度，适合快速扫读。
- 点击 tile 切换选中模块。

Tile 内容：

```text
Slot 1
32 GB DDR5 SO-DIMM
Micron CT32G56C46S5.M16D1
5600 MT/s
```

状态：

- 选中模块高亮。
- 缺失字段显示 `未识别`。
- 如果模块容量/速度/料号与其他模块不一致，tile 上显示小 warning badge。

### Module Identity

对应 CPU-Z SPD 顶部和 AIDA64 `内存模块`。

字段：

| Label | Example | Source Strategy |
| --- | --- | --- |
| 插槽 | Channel B / DIMM 0 | WMI DeviceLocator/BankLabel |
| 模块名称 | Micron CT32G56C46S5.M16D1 | WMI PartNumber / SPD |
| 模块容量 | 32 GB | WMI |
| 模块类型 | SO-DIMM | WMI FormFactor / SPD |
| 存取类型 | DDR5 SDRAM | WMI/SMBIOS / SPD |
| 最大带宽 | DDR5-5600 (2800 MHz) | WMI speed / SPD |
| 模块制造商 | Micron Technology | WMI Manufacturer / SPD |
| DRAM 制造商 | Micron | SPD |
| 型号 / Part Number | CT32G56C46S5.M16D1 | WMI PartNumber / SPD |
| 序列号 | EB235139 | WMI SerialNumber / SPD |
| 周次 / 年份 | Week 04 / 2025 | SPD |
| Revision | 3.1 | SPD |

### Module Organization

对应 HWiNFO 的 `内存模块特征`。

字段：

- Rank Mix。
- Rank Count。
- Bank Group Count。
- Banks Per Group。
- Row Address Bits。
- Column Address Bits。
- Device Width。
- Bus Width。
- Data Width。
- Total Width。
- Module Organization。
- SDRAM Die Count。
- ECC / Error Correction。
- On-die ECC, if available。

第一版可先显示 WMI 的 `DataWidth` / `TotalWidth`，其余标记为 `待接入 SPD 读取`。

### Voltages And Features

对应 AIDA64/HWiNFO 的电压和 DDR5 feature bits。

字段：

- VDD nominal / operable / endurance。
- VDDQ nominal / operable / endurance。
- VPP nominal / operable / endurance。
- Configured voltage。
- Min voltage / Max voltage。
- PMIC vendor / revision, future。
- Write Temperature Sense。
- Bounded Fault。
- BL32。
- Non-Standard Core Timings。
- HeatSpreader。
- Asymmetrical Module。

显示方式：

- 电压用普通字段行。
- Feature bits 用 yes/no 状态行，支持 `支持`、`不支持`、`未识别`。

### Timing Profiles

对应 CPU-Z SPD 的 timing table、AIDA64/HWiNFO 的 supported timings。

表格列：

- Profile：`JEDEC #7`、`JEDEC #8`、`JEDEC #9`、`XMP #1`、`EXPO #1`。
- Frequency：`2800 MHz`。
- Effective Rate：`DDR5-5600`。
- CL。
- tRCD。
- tRP。
- tRAS。
- tRC。
- tRFC1 / tRFC2 / tRFCsb。
- tRRD_L。
- tCCD_L。
- tCCD_L_WR。
- tFAW。
- Voltage。

桌面布局：

- 主要 timing 使用横向表格，适合对比 profile。
- 过多字段时允许水平滚动。
- 支持 profile 数量过多时用 `主要` / `高级` 切换，第一版可以只显示主要字段。

窄宽度布局：

- 改为每个 profile 一张紧凑 profile card，避免表格挤爆。

### Diagnostics And Notes

说明字段：

- 数据源：WMI/SMBIOS、SPD、推导、待接入。
- SPD 读取状态：未接入、读取失败、平台屏蔽、权限不足、校验失败。
- 混插提示。
- 速度/时序解释：WMI speed 不一定等于当前运行态频率。
- 刷新时间。

## Visual Design

页面应保持 HwScope 当前风格：WPF-UI / Fluent、动态主题 token、高密度但不拥挤。

### Desktop Layout

```text
----------------------------------------------------------------
内存 / SPD                                      [Refresh][Copy][Save]
64 GB DDR5 · 2 modules · DDR5-5600
[DDR5] [64 GB] [2 x 32 GB] [5600 MT/s] [SPD 待接入]

----------------------------------------------------------------
| 运行态概览                                                   |
| Type DDR5   Size 64 GB   Channels 待接入   Rate 5600 MT/s    |
| Frequency 待接入   CL 待接入   tRCD 待接入   tRP 待接入 ...   |

----------------------------------------------------------------
| Slot 1 · 32 GB · Micron CT32... | Slot 2 · 32 GB · Micron ... |

----------------------------------------------------------------
| 模块身份                           | 模块组织                 |
| Slot             Channel A DIMM 0   | Rank Count      待接入   |
| Part Number      CT32G56C46S5...    | Data Width      64 bit   |
| Serial           EB235139           | Total Width     64 bit   |
| Week/Year        待接入 SPD         | Bank Groups     待接入   |

----------------------------------------------------------------
| Timing Profiles                                               |
| Profile      Freq   CL  tRCD  tRP  tRAS  tRC  Voltage         |
| JEDEC #7     2500   40  40    40   80    120  1.10 V          |
| JEDEC #8     2633   42  43    43   85    127  1.10 V          |

----------------------------------------------------------------
| 电压和特性                         | 数据说明                 |
```

### Component Choices

- 页面使用 `UserControl`，类似 `CpuDetailPage`，不使用独立窗口。
- 使用 page-level section cards，保持 7-8 px radius。
- 不嵌套 cards；模块 tiles 和 timing profiles 是 repeated items，可以用卡片。
- Refresh/Copy/Save 使用 WPF-UI button + SymbolIcon。
- 模块选择使用 tile list 或 segmented selector，不使用传统 ComboBox 作为唯一入口；ComboBox 可作为高模块数量 fallback。
- Timing profiles 使用 DataGrid-like table，但视觉上应轻量，避免默认 WPF DataGrid 的厚重边框。
- 所有颜色使用 `DynamicResource` token。

### Density

推荐尺寸：

- Page title: 24 px。
- Section title: 14-15 px semibold。
- Field label: 13 px。
- Field value: 13-14 px。
- Row height: 30-34 px。
- Module tile width: 260-320 px。
- Section card radius: 7 px。
- 桌面宽度 >= 1100 px 时，模块身份/组织/电压分两列。
- 低于 900 px 时单列堆叠。

### Missing Values

不要留空。

使用：

- `未识别`：已尝试读取但不可用。
- `不支持`：硬件/API 明确不支持。
- `待接入 SPD 读取`：需要 raw SPD provider。
- `待接入内存控制器读取`：需要运行态 controller provider。
- `推导值`：来自容量/速度等字段推算。

## Interaction Design

### Navigation

当前 `MainWindow.xaml` 中 `硬件 -> 内存` 仍路由到 summary：

```xml
<ui:NavigationViewItem Content="内存"
                       Tag="summary"
                       Icon="{ui:SymbolIcon Database24}" />
```

新页面应改为：

```xml
<ui:NavigationViewItem Content="内存"
                       Tag="memory-detail"
                       Icon="{ui:SymbolIcon Database24}" />
```

并在 `MainWindow.xaml.cs` 增加 `MemoryDetailPage` 单例和 route。

### Refresh

刷新语义保持全局：

- 页面首次加载：`App.HardwarePreload.EnsureLoadedAsync()`。
- 手动刷新：`App.HardwarePreload.RefreshAsync()`。
- 后续 SPD/native provider 加入后，刷新应同时刷新 inventory 和 SPD snapshot。
- 刷新过程中保留旧值，按钮 disabled，状态栏显示当前动作。

### Module Selection

- 默认选中第一个可用模块。
- 用户点击模块 tile 切换右侧详情。
- 如果 refresh 后模块列表变化，优先按 `DeviceLocator + SerialNumber + PartNumber` 复原之前选中模块。
- 如果无法复原，选中第一个模块。

### Copy

复制输出稳定文本报告：

```text
Memory / SPD
Summary
  Type: DDR5 [WMI]
  Total Capacity: 64 GB [WMI]
  Modules: 2 [WMI]
  Configured Speed: 5600 MT/s [WMI]

Module 1
  Slot: Channel A / DIMM 0 [WMI]
  Capacity: 32 GB [WMI]
  Manufacturer: Micron Technology [WMI]
  Part Number: CT32G56C46S5.M16D1 [WMI]
  Serial: EB235139 [WMI]
  Timing Profiles: Pending SPD reader

Generated At: 2026-07-05 10:30:00
```

### Save

第一版直接保存 `.txt`，内容与 Copy 一致。

未来可扩展：

- JSON 导出。
- Raw SPD binary dump。
- HTML/PDF 报告。

## Data Model Proposal

新增模型应放在 Core：

```text
src/HwScope.Core/Hardware/Memory/
  MemoryDetailReport.cs
  MemoryDetailCollector.cs
  MemoryDetailReportFormatter.cs
  MemoryDataSource.cs
  MemoryFieldValue.cs
  MemoryTypeFormatter.cs
```

建议模型：

```csharp
public sealed record MemoryDetailReport(
    MemorySummary Summary,
    MemoryRuntimeInfo Runtime,
    IReadOnlyList<MemoryModuleDetail> Modules,
    IReadOnlyList<MemoryDataNote> Notes,
    DateTimeOffset GeneratedAt);

public sealed record MemorySummary(
    MemoryFieldValue<string> Type,
    MemoryFieldValue<string> TotalCapacity,
    MemoryFieldValue<int> ModuleCount,
    MemoryFieldValue<string> Layout,
    MemoryFieldValue<string> ConfiguredSpeed,
    MemoryFieldValue<string> ChannelMode);

public sealed record MemoryRuntimeInfo(
    MemoryFieldValue<double> ClockMHz,
    MemoryFieldValue<string> EffectiveRate,
    MemoryFieldValue<string> Ratio,
    MemoryTimingValues PrimaryTimings);

public sealed record MemoryModuleDetail(
    string Id,
    MemoryModuleIdentity Identity,
    MemoryModuleOrganization Organization,
    MemoryModuleVoltages Voltages,
    IReadOnlyList<MemoryTimingProfile> TimingProfiles,
    IReadOnlyList<MemoryModuleFeature> Features,
    IReadOnlyList<MemoryDataNote> Notes);
```

字段值与 CPU 页类似：

```csharp
public sealed record MemoryFieldValue<T>(
    T? Value,
    string DisplayText,
    MemoryDataSource Source,
    bool IsAvailable,
    bool IsEstimated = false,
    string? Note = null);
```

来源枚举：

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

Timing profile：

```csharp
public sealed record MemoryTimingProfile(
    string Name,
    MemoryFieldValue<double> FrequencyMHz,
    MemoryFieldValue<string> EffectiveRate,
    MemoryFieldValue<double> CasLatency,
    MemoryFieldValue<int> Trcd,
    MemoryFieldValue<int> Trp,
    MemoryFieldValue<int> Tras,
    MemoryFieldValue<int> Trc,
    MemoryFieldValue<string> Voltage,
    MemoryDataSource Source);
```

## Inventory Changes

当前 `MemoryModuleSnapshot` 只有：

```csharp
Capacity,
Speed,
ConfiguredClockSpeed,
SmbiosMemoryType,
MemoryType
```

Stage 1/2 应扩展为：

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

WMI query 扩展：

```sql
SELECT Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, MemoryType,
       Manufacturer, PartNumber, SerialNumber, BankLabel, DeviceLocator,
       FormFactor, DataWidth, TotalWidth, ConfiguredVoltage, MinVoltage,
       MaxVoltage, MemoryTypeDetail, InterleavePosition, Tag
FROM Win32_PhysicalMemory
```

注意：这些仍不是 raw SPD。它们是 Windows/SMBIOS 暴露的信息，质量取决于 BIOS/厂商。

## Collection Strategy

### Stage 1 Sources

使用当前共享 inventory：

- `HardwareInventoryCollector` 扩展 `Win32_PhysicalMemory` 字段。
- `MemoryDetailCollector.CreateReport(HardwareInventorySnapshot snapshot)` 从 snapshot 构建报告。
- 页面不直接 WMI 查询。

可真实展示：

- total capacity。
- module count。
- per-module capacity。
- memory type。
- configured speed。
- manufacturer。
- part number。
- serial number。
- bank/device locator。
- data/total width。
- voltage fields, if WMI returns。

### Stage 3 SPD Provider

推荐独立 provider：

```csharp
public interface ISpdProvider
{
    SpdProviderResult TryCollect();
}
```

Native worker 查找路径参考 memory benchmark：

```text
AppContext.BaseDirectory\spd.exe
AppContext.BaseDirectory\native\spd.exe
src\HwScope.Native.Spd\build\Release\spd.exe
```

不要使用用户机器绝对路径。

失败类型要结构化：

- WorkerMissing。
- AccessDenied。
- PlatformBlocked。
- UnsupportedMemoryType。
- ChecksumFailed。
- ParseFailed。

## UI Implementation Plan

### Files To Add

```text
src/HwScope.Core/Hardware/Memory/MemoryDetailReport.cs
src/HwScope.Core/Hardware/Memory/MemoryDetailCollector.cs
src/HwScope.Core/Hardware/Memory/MemoryDetailReportFormatter.cs
src/HwScope.App/Pages/MemoryDetailPage.xaml
src/HwScope.App/Pages/MemoryDetailPage.xaml.cs
docs/memory-spd-detail-page-design.md
```

### Page Code-Behind Shape

保持与 `CpuDetailPage` 一致：

```csharp
private readonly MemoryDetailCollector _reportBuilder = new();
private MemoryDetailReport? _currentReport;
private string? _selectedModuleId;
private int _refreshVersion;
private bool _loadedOnce;

public async Task RefreshAsync(bool forceRefresh)
{
    var version = Interlocked.Increment(ref _refreshVersion);
    SetBusy(true);
    try
    {
        var snapshot = forceRefresh
            ? await App.HardwarePreload.RefreshAsync()
            : await App.HardwarePreload.EnsureLoadedAsync();
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

### XAML Structure

```text
Grid
  Header/action bar
  ScrollViewer
    StackPanel
      Summary band
      Runtime timings section
      Module selector ItemsControl
      Responsive details WrapPanel
        Module Identity section
        Module Organization section
        Voltages / Features section
      Timing profile table section
      Notes section
```

复用 CPU 页风格：

- `MemorySectionBorderStyle`
- `MemorySourceBadgeStyle`
- `MemoryFieldRowView`
- `MemorySectionView`
- `MemoryModuleTileView`
- `MemoryTimingProfileView`

## First Version Field Mapping

第一版可交付字段：

| UI Field | Existing Snapshot | Needs Snapshot Extension | Future Provider |
| --- | --- | --- | --- |
| Total Capacity | Yes | No | No |
| Type | Yes | No | SPD can improve |
| Module Count | Yes | No | No |
| Configured Speed | Yes | No | SPD can improve |
| Module Capacity | Yes | No | No |
| Manufacturer | No | Yes | SPD can improve |
| Part Number | No | Yes | SPD can improve |
| Serial Number | No | Yes | SPD can improve |
| Slot / Locator | No | Yes | No |
| Form Factor | No | Yes | SPD can improve |
| Data / Total Width | No | Yes | SPD can improve |
| Voltage | No | Yes | SPD can improve |
| Channel Mode | No | Partial computed | Memory controller |
| Current Timings | No | No | Memory controller |
| JEDEC Profiles | No | No | SPD |
| XMP / EXPO | No | No | SPD |
| DDR5 Features | No | No | SPD |

## Empty States

没有模块：

```text
未检测到内存模块
Windows WMI 没有返回 Win32_PhysicalMemory 数据。可以刷新重试。
```

SPD 不可用：

```text
SPD 读取尚未接入
当前页面显示 Windows/SMBIOS 提供的模块信息；JEDEC/XMP/EXPO 时序需要后续 SPD provider。
```

平台屏蔽：

```text
SPD 读取被平台屏蔽
某些笔记本、OEM BIOS 或权限策略会阻止 SMBus/SPD 读取。
```

## Diagnostics

页面 notes 应包含：

- WMI 查询步骤状态。
- 每根模块缺失字段数量。
- 模块不一致提示：
  - capacity mismatch。
  - configured speed mismatch。
  - memory type mismatch。
  - manufacturer/part number mismatch。
- SPD provider 状态。
- 运行态 timing provider 状态。

不要把混插提示做成错误；它只是诊断信息。

## Acceptance Criteria

Stage 1：

- `硬件 -> 内存` 打开 Memory / SPD 详情页。
- 页面从共享 preload snapshot 构建，不直接同步 WMI。
- 页面展示内存总览、模块列表、选中模块详情。
- Refresh 触发全局 inventory refresh。
- Copy/Save 输出文本报告。
- WMI/SMBIOS/占位字段有来源标记。
- 没有 SPD provider 时，JEDEC/XMP/EXPO 区域清楚显示 `待接入 SPD 读取`。
- `dotnet build` 通过。

Stage 2：

- `MemoryModuleSnapshot` 扩展更多 `Win32_PhysicalMemory` 字段。
- 页面显示 Manufacturer、PartNumber、SerialNumber、BankLabel、DeviceLocator、FormFactor、DataWidth、TotalWidth、电压字段。
- 混插提示可见。

Stage 3：

- Native SPD worker 输出 schema-versioned JSON。
- SPD 失败不影响页面显示 WMI-backed 字段。
- JEDEC timing table 有真实 profile。
- DDR5 电压/组织/feature bits 由 SPD 填充。

Stage 4：

- 当前频率和当前 primary timings 由真实运行态 provider 填充。
- CPU-Z Memory 页核心字段不再是占位。

## Recommended Implementation Sequence

1. 扩展 `MemoryModuleSnapshot` 和 `HardwareInventoryCollector` 的 WMI 字段。
2. 新增 Core memory detail models、collector、formatter。
3. 新增 `MemoryDetailPage`，先使用 WMI/SMBIOS 字段和清晰占位。
4. 接入主窗口导航，把 `硬件 -> 内存` 改到 `memory-detail`。
5. 更新 README / architecture docs。
6. 设计 native SPD worker contract，不急着实现底层读取。
7. 接入 SPD timing profiles 和 DDR5 组织字段。
8. 后续再接运行态 timing provider。

## Risks And Mitigations

### WMI Field Quality

风险：WMI/SMBIOS 字段经常为空、填错或被 OEM 简化。

缓解：

- 字段级来源标记。
- 不把 WMI speed 称作当前实时频率。
- 缺失字段显示明确状态。

### SPD Access Restrictions

风险：Windows 普通用户态无法稳定读取所有平台 SPD，笔记本 LPDDR/onboard memory 可能没有可访问 SPD EEPROM。

缓解：

- SPD provider 是可选增强，不作为页面可用性的前提。
- 失败结构化，UI 显示平台/权限限制。

### DDR Generational Complexity

风险：DDR4、DDR5、LPDDR、RDIMM、ECC、CAMM 等字段差异大。

缓解：

- 模型使用可选字段和 feature flags。
- UI 按字段可用性显示，不强行套固定 DDR5 布局。
- 高级字段放在折叠或后置 section。

### Runtime Timing Accuracy

风险：当前时序/频率不是 SPD 静态数据，必须从内存控制器或平台寄存器读取。

缓解：

- Runtime Overview 和 SPD Timing Profiles 明确分区。
- `Configured Speed`、`SPD Max Bandwidth`、`Current Clock` 三者分开命名。

### UI Density

风险：HWiNFO 级字段非常多，直接平铺会压垮页面。

缓解：

- 顶部只放关键运行态字段。
- 模块详情分组。
- timing table 默认显示主要字段，高级字段后续可折叠。
