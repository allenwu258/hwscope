# Storage Detail Page Requirements And UI Design

本文档定义 HwScope 真正的存储设备详情页需求、信息架构、数据边界和 UI 设计。参考对象是 CrystalDiskInfo 的单盘详情页，同时结合 HwScope 已有的硬件预加载、字段来源标记、CPU/内存详情页和 Fluent/WPF shell。

完整工程落地方案见 `docs/storage-detail-implementation-plan.md`。

目标不是复刻 CrystalDiskInfo 的渐变皮肤，也不是只把 `Win32_DiskDrive` 的四个字段放进一张卡片。目标是建立一个能够区分物理磁盘、协议、控制器、卷、健康状态和原始 SMART/Health 数据的存储诊断页面。

## Current Implementation Status

截至 2026-07-14，首个完整实现已经落地：

- 独立 `StorageDetailPage`、`storage-detail` 导航、物理设备 selector、健康概要、详情 section、SMART table 和物理分区/卷组合列表。
- enriched `DiskDriveSnapshot`、Windows Storage descriptor/alignment/TRIM 查询和 `MSFT_Partition` / `MSFT_Volume` 映射。
- NVMe SMART / Health Information log page 0x02 的真实读取、解析、健康判定和 128-bit counter 格式化。
- ATA SMART RETURN STATUS、attribute/threshold parser、sector checksum、legacy SMART/ATA pass-through provider 和保守健康判定。
- `StorageDetailService` 的同设备 active-task 合并、跨设备隔离、5 秒 soft timeout、stale selection 防护和页面重入恢复。
- model/firmware/serial 字段分别保留最终数据来源。
- `storage list` 使用轻量 descriptor 展示真实 bus；`storage --disk N` 支持文本和 JSON 输出。
- 33 项 Core tests 覆盖 descriptor/NVMe/ATA parser、overall/health evaluator、soft timeout、字段来源合并、bus formatting 和 formatter。

当前 Samsung PM9F1 NVMe 已完成真实硬件验证。ATA provider 已完成合成 fixture 测试，但仍需直连 SATA SSD/HDD 硬件验证。USB/RAID/Storage Spaces 的协议透传仍是后续覆盖工作。

当前仍未完成 NVMe Identify/controller/namespace 和 PCIe negotiated/max link；同步 IOCTL 只有 UI soft timeout，没有 kernel I/O 硬取消。设备移除后保留最后报告并标记断开、USB/SAT 广覆盖和可终止 worker 隔离仍属于后续工作。

## Baseline At Design Creation

设计建立时，仓库的存储能力仅用于硬件摘要：

- `HardwareInventoryCollector` 当时只查询基础 `Win32_DiskDrive` 字段。
- `DiskDriveSnapshot` 当时只有 `Model`、`Size`、`MediaType`、`InterfaceType`。
- `HardwareCollector.CreateSummary()` 只把磁盘格式化为 `型号（容量）` 字符串。
- 主窗口 `硬件 -> 存储设备` 当时路由到 `summary`。
- 当时没有物理磁盘选择、固件/序列号、总线链路、卷映射、温度、寿命、通电时间或 SMART/NVMe Health 读取。

这一历史基线说明了为什么需要新增独立领域模型和按协议读取能力；它不描述当前实现。

## Goals

存储详情页要解决以下问题：

- 让用户快速确认系统中有哪些物理存储设备，以及每块设备的健康状态、温度、容量和挂载卷。
- 展示选中设备的型号、固件、序列号、接口、总线、当前/最大链路、协议标准和支持特性。
- 对 NVMe 和 ATA/SATA 使用真实协议数据展示寿命、读写量、通电次数、通电时间和错误计数。
- 保留原始 SMART/NVMe Health 值，方便高级用户验证、截图、复制和故障排查。
- 明确区分物理磁盘、分区、卷和盘符，避免把 `C:` 当成物理设备身份。
- 明确标记数据来源、解释方式和置信度，不伪造通用健康百分比。
- 为后续温度历史、磁盘自检、通知、报告和稳定性功能建立结构化数据基础。

## Non-Goals

第一阶段不做以下内容：

- 不实现磁盘测速或写入型 benchmark；存储详情页是只读诊断页。
- 不发送格式化、安全擦除、Sanitize、TRIM、固件升级或 namespace management 命令。
- 不自动启动 ATA/NVMe 长短自检。
- 不默认持续轮询或唤醒处于休眠状态的机械硬盘。
- 不把 USB bridge、RAID 卡或厂商私有属性无法读取解释为磁盘损坏。
- 不承诺完全复现 CrystalDiskInfo 的厂商私有属性数据库和专有健康算法。
- 不把 WMI `Status`、PnP 状态或卷文件系统状态当成 SMART 健康状态。
- 不在没有真实协议数据时生成温度、寿命或健康百分比。

## CrystalDiskInfo Reference Analysis

参考页面包含四层信息。

### Device Switcher

窗口顶部按物理磁盘显示：

- 健康状态。
- 温度。
- 关联盘符。
- 当前选中状态。

这个设计非常适合多盘机器，因为用户不需要先打开下拉框才能发现其他磁盘。HwScope 应保留“可见的物理盘切换条”，但使用固定尺寸 tile、语义状态色和清晰选中边框，不使用装饰性渐变圆球。

### Identity And Link Summary

标题和上半区展示：

- 型号与容量。
- 固件、序列号。
- 接口和传输模式。
- 驱动器号。
- 协议标准和支持功能。
- 主机读取/写入、转速、通电次数和通电时间。

这部分把“设备是什么”“如何连接”“使用了多久”放在同一视图中，是页面最值得保留的信息组织方式。

### Health And Temperature

CrystalDiskInfo 把健康与温度放在左侧大块区域，便于第一眼识别异常。HwScope 也应让健康与温度成为首屏强信号，但不应只用颜色表达，也不应让健康百分比看起来比协议事实更可靠。

### Attribute Table

底部表格展示协议属性和原始值。参考截图中的 NVMe 表包含 ID、属性名称和原始值；ATA SMART 通常还需要当前值、最差值和阈值。

HwScope 不应强迫所有协议使用同一组列：

- NVMe Health 是标准化 log page 字段，主要展示解码值、单位和原始值。
- ATA SMART 是 attribute ID + normalized value + threshold + vendor raw bytes。
- SCSI/SAS health 和 sense/log page 结构不同，应单独适配或明确显示暂不支持。

## Product Scope

### Stage 1: WMI And Storage Inventory Detail

先完成真正可用的物理磁盘详情页骨架和可靠的设备/卷映射。

必须展示：

- 物理磁盘列表和选择。
- 磁盘编号、设备路径、型号、容量、介质类型和总线类型。
- 固件、序列号、PnP/Device Instance ID，能读取时展示。
- 分区、卷标、文件系统、容量、可用空间和盘符。
- 系统盘、启动盘、分页文件等角色提示，能可靠判断时展示。
- 数据来源和读取错误。
- 刷新、复制和保存文本报告。

Stage 1 不应显示伪造健康状态。未读取 SMART/Health 时显示 `健康数据暂不可用`，而不是默认“良好”。

### Stage 2: Standard Windows Storage Protocol Data

使用 Windows 文档化存储 API 补充设备身份和链路事实：

- `IOCTL_STORAGE_QUERY_PROPERTY`。
- `StorageDeviceProperty`。
- `StorageAdapterProperty`。
- `StorageDeviceProtocolSpecificProperty`，用于支持的平台和协议。
- NVMe Identify Controller / Namespace 数据。
- NVMe SMART / Health Information log page。

目标字段：

- Bus type：NVMe、SATA、SAS、USB、RAID、Virtual 等。
- 协议版本和标准。
- 当前/最大链路速度和宽度，能够可靠读取时展示。
- Logical/Physical sector size。
- TRIM、volatile write cache、SMART/self-test 等 capability。
- NVMe namespace、controller、firmware slot 等基础信息。
- NVMe 标准健康、温度、寿命和使用计数器。

### Stage 3: ATA SMART

为直连 ATA/SATA 设备接入标准 SMART 数据：

- SMART overall status。
- Attribute ID。
- Current normalized value。
- Worst normalized value。
- Threshold。
- Raw bytes 和标准十六进制文本。
- 常见标准属性的保守解码，例如 power-on hours、power cycle count、temperature、reallocated sectors、pending sectors、uncorrectable sectors。

ATA raw value 经常具有厂商语义，必须同时保留原始 bytes。没有明确规则时只展示 raw，不把数值解释成统一单位。

### Stage 4: Bridge, RAID And Platform Coverage

扩展复杂拓扑：

- USB-SATA SAT bridge。
- USB-NVMe bridge。
- Storage Spaces / `MSFT_PhysicalDisk`。
- RAID 控制器透传。
- SAS/SCSI log sense。
- 虚拟磁盘、VHD/VHDX 和云主机设备。

每类设备都需要支持矩阵。读取失败应显示“控制器/桥接不支持透传”，不能归因于磁盘故障。

### Stage 5: Monitoring And Diagnostics

后续能力：

- 可选温度历史和健康事件历史。
- 温度/健康通知。
- 用户主动启动的只读短自检，并提供明确风险提示。
- 导出 JSON。
- 与稳定性报告关联。

## Data Source Strategy

存储页需要组合多个 Windows 数据源，但必须保留各自语义，不能用一个来源覆盖另一个来源的事实。

| Source | Primary Responsibility | Must Not Be Treated As |
| --- | --- | --- |
| `Win32_DiskDrive` | 启动期物理盘枚举、型号、容量、DeviceID/Index、基础接口字段 | 原生 SMART 健康 |
| `MSFT_Disk` | 现代存储栈身份、BusType、FirmwareVersion、sector size、partition style、系统/启动角色 | NVMe/ATA 原始 health log |
| `MSFT_PhysicalDisk` | Storage Spaces/池化设备、MediaType、OperationalStatus、HealthStatus | 所有直连磁盘的统一 SMART 替代品 |
| `MSFT_Partition` / `MSFT_Volume` | 物理盘到分区、卷、文件系统、盘符和容量映射 | 物理设备身份 |
| Storage property IOCTL | 设备 descriptor、adapter、protocol、sector/alignment 和 capability | 厂商健康算法 |
| NVMe protocol query | Identify 和标准 SMART/Health Information log | ATA SMART attribute |
| ATA SMART/pass-through | ATA overall status、attribute、threshold 和 raw bytes | 通用跨厂商剩余寿命百分比 |
| SCSI/SAS log sense | SCSI/SAS 协议健康和错误页，后续实现 | ATA/NVMe 字段模型 |

### Baseline Enumeration Fields

建议扩展启动期 `DiskDriveSnapshot`：

```text
Index
DeviceId
PnpDeviceId
Model
FirmwareRevision
SerialNumber
Size
MediaType
InterfaceType
BytesPerSector
Partitions
ScsiBus
ScsiPort
ScsiTargetId
ScsiLogicalUnit
```

这些字段用于建立稳定枚举和后续 provider 匹配。WMI 缺失或格式异常时不应阻止 Storage API 再识别。

### Modern Storage Management Fields

在支持 `root/Microsoft/Windows/Storage` 的系统上，可以使用：

```text
MSFT_Disk
  Number
  Path
  FriendlyName
  SerialNumber
  FirmwareVersion
  Manufacturer
  Model
  Size
  BusType
  PartitionStyle
  LogicalSectorSize
  PhysicalSectorSize
  IsBoot
  IsSystem
  IsOffline
  IsReadOnly
  OperationalStatus
  HealthStatus

MSFT_Partition
  DiskNumber
  PartitionNumber
  DriveLetter
  AccessPaths
  Offset
  Size
  GptType / MbrType
  IsBoot / IsSystem / IsHidden

MSFT_Volume
  Path
  DriveLetter
  FileSystem
  FileSystemLabel
  Size
  SizeRemaining
  HealthStatus
  OperationalStatus
```

这些 management fields 适合身份、角色和卷映射。`HealthStatus` 应显示为 `Windows Storage` 来源；只有原生协议 provider 成功时，页面才显示 `NVMe` 或 `ATA SMART` 健康。

### Source Precedence

同一字段有多个来源时采用以下原则：

1. 原生协议 identity 优先用于固件、序列号、协议版本和 capability。
2. Storage property descriptor 次之。
3. `MSFT_Disk` 用于现代存储管理身份、磁盘角色和 sector 信息。
4. `Win32_DiskDrive` 用于兼容枚举和 fallback。
5. 型号字符串推断只能作为标记清晰的最后 fallback，不能推导健康、温度或链路事实。

合并结果必须保留最终字段来源；发生冲突时记录 diagnostic，不静默选择后丢弃差异。

## Physical Disk, Partition And Volume Boundaries

页面的选择单位必须是物理磁盘或由存储栈暴露的物理设备，不是盘符。

```text
Physical Disk 0
  Controller / Bus / Protocol
  Namespace or media
  Partition 1
    Volume C:
  Partition 2
    Recovery volume
```

要求：

- 同一个物理磁盘可以关联多个分区和盘符。
- 一个卷可能没有盘符，仍应显示 volume GUID 或卷标。
- Storage Spaces、动态磁盘和 RAID 可能不是一对一映射，UI 要显示 `虚拟/池化设备`，不要编造单盘归属。
- NVMe controller 和 namespace 不是完全相同的概念。第一版可按 Windows 暴露的 physical drive 展示，但模型中应预留 controller/namespace identity。
- 稳定设备 ID 不应只使用列表索引。优先使用 device instance ID、physical drive number、serial 和 protocol identifier 的组合。

## Information Architecture

页面采用“设备选择 + 健康概要 + 身份/链路 + 寿命统计 + 协议属性 + 卷映射 + 数据说明”的单页诊断结构。

```text
Storage Detail Page
  Header
    Title, refresh time, Refresh, Copy, Save

  Physical Disk Selector
    Disk tile: model, capacity, health, temperature, volumes

  Selected Disk Summary
    Model and capacity
    Health / remaining life
    Temperature
    Identity and firmware
    Interface and negotiated link
    Host read/write and power-on statistics

  Protocol Health Table
    NVMe Health fields or ATA SMART attributes

  Volumes And Partitions
    partition, volume, filesystem, capacity, free space, roles

  Notes And Diagnostics
    source, unsupported reason, interpretation notes, refresh time
```

## Header

标题：`存储设备`。

副标题根据当前状态显示：

```text
3 个物理设备 · 检测时间 2026-07-13 23:40:12
```

动作：

- Refresh：刷新当前设备的动态 health，同时同步必要的设备身份。
- Copy：复制当前选中设备的稳定文本报告。
- Save：保存 `.txt` 报告。

页面工具栏后续可增加 JSON 导出，但不应放置无功能按钮。CLI 已支持 `storage --disk N --json`。

## Physical Disk Selector

使用横向可换行的固定尺寸 tile。推荐尺寸：

```text
Width: 220-260 px
MinHeight: 82 px
Radius: 7 px
```

Tile 内容：

```text
Disk 0 · NVMe
Samsung PM9F1 2048 GB
良好 · 49 C
C:
```

状态：

- 选中：使用现有 `HwScopeActiveViewBrush` 和清晰边框。
- 良好：绿色语义图标/文字。
- 注意：黄色语义图标/文字。
- 严重：红色语义图标/文字。
- 未知/不支持：中性色和明确文本。
- 正在读取：tile 尺寸不变，显示小型进度状态。

机器有大量磁盘时：

- 前几块设备保持 tile 展示。
- 允许换行。
- 后续可增加搜索或紧凑列表模式。
- 不依赖仅显示盘符的窄 tab，因为无盘符磁盘同样重要。

## Selected Disk Summary

### Primary Identity

主标题：

```text
PM9F1 SED Samsung 2048GB
```

辅助文本：

```text
Disk 0 · 2.05 TB · NVMe SSD · C:\
```

不把十进制厂商容量和二进制 Windows 容量混成一个数值。建议同时采用：

- 标题使用厂商十进制容量，例如 `2.05 TB`。
- tooltip 或详情行显示精确 bytes 和二进制 `1.86 TiB`。

### Health Status

状态枚举：

```text
Good
Caution
Critical
Unknown
Unsupported
```

UI 展示中文：

```text
良好
注意
严重
未知
不支持
```

健康块必须同时包含图标、文字和解释，不只依赖颜色。

健康百分比规则：

- NVMe 可以根据标准 `Percentage Used` 显示 `预计剩余寿命 97%`，计算为 `100 - clamp(PercentageUsed, 0, 100)`。
- `Percentage Used` 可能超过 100，原始值仍应保留；UI 剩余寿命最低显示 0%。
- ATA 不存在通用、跨厂商可靠的剩余寿命百分比。没有明确厂商规则时只显示 Good/Caution/Critical，不显示百分比。
- WMI/PnP 正常不等于 SMART Good。

### Temperature

显示：

- 当前综合温度。
- NVMe 可选展示传感器 1-N。
- ATA 可展示已可靠解码的 temperature attribute。
- 原始 Kelvin/Celsius 数据来源和转换留在详情/tooltip。

状态阈值不应硬编码成所有设备统一事实。第一版可以使用保守的 UI 提示阈值，同时明确这只是应用提示：

```text
Normal: below 60 C
Warm: 60-69 C
Hot: 70 C or above
```

如果设备提供 warning/critical composite temperature threshold，应优先使用设备阈值。

无有效温度时显示 `未报告`，不能显示 `0 C`。

### Identity Fields

字段：

- 型号。
- 固件版本。
- 序列号。
- 设备实例 ID。
- Physical Drive 路径。
- 磁盘编号。
- 控制器/namespace 标识，能读取时展示。
- 容量和精确 bytes。
- 介质类型：SSD、HDD、SCM、Virtual、Unknown。
- 转速：HDD 显示 RPM；SSD 显示 `SSD / 无旋转介质`，不显示虚构转速。

### Interface And Link Fields

字段：

- 总线类型。
- 接口/协议：NVMe、ATA、SCSI 等。
- 当前传输模式。
- 最大传输模式。
- 当前 PCIe generation / lane width。
- 最大 PCIe generation / lane width。
- SATA negotiated speed / maximum speed。
- 标准版本，例如 `NVM Express 2.0`、`ACS-4`。
- Logical sector size。
- Physical sector size。
- Alignment。
- 支持功能：SMART、TRIM、volatile write cache、self-test、sanitize capability 等。

无法从标准 API 可靠获得链路速度时显示 `未报告`，不要从型号猜测。

### Lifetime And Usage Fields

通用展示：

- Host Reads。
- Host Writes。
- Power Cycle Count。
- Power-On Hours。
- Unsafe Shutdowns。
- Media/Data Integrity Errors。
- Error Information Log Entries。

NVMe 补充：

- Data Units Read/Written。
- Host Read/Write Commands。
- Controller Busy Time。
- Available Spare。
- Available Spare Threshold。
- Percentage Used。
- Critical Warning bits。

ATA 补充：

- Reallocated Sector Count。
- Current Pending Sector Count。
- Offline Uncorrectable。
- UDMA CRC Error Count。
- Start/Stop Count。
- Load/Unload Cycle Count，设备提供时展示。

所有累计值应提供格式化值和原始值。大计数转换为 GB/TB 时必须记录采用的协议单位，不能把 NVMe data unit 当成普通 sector。

## Health Evaluation

### NVMe

健康状态优先级：

1. 无法读取 log page：`Unknown` 或 `Unsupported`。
2. `Critical Warning` 中只读模式、可靠性下降等严重位：`Critical`。
3. Available Spare 低于 threshold、温度超设备 critical threshold、明显 media/data error：至少 `Caution`，具体严重级别保守处理。
4. `Percentage Used >= 100`：`Caution`，除非同时存在严重 warning。
5. 其余标准字段正常：`Good`。

Critical Warning 必须逐 bit 展示，不能只显示一个十六进制值。

### ATA SMART

健康状态优先级：

1. SMART overall status 明确失败：`Critical`。
2. 任一 pre-fail attribute 的 normalized value 达到或低于有效 threshold：`Critical`。
3. Pending/Reallocated/Uncorrectable 等关键属性出现非零值：按保守规则标为 `Caution`，并显示触发原因。
4. SMART 可读但没有异常：`Good`。
5. 透传不支持或属性表不可读：`Unknown/Unsupported`。

ATA vendor raw bytes 的布局不统一。健康判定必须基于有明确语义的属性和 overall status，不能对所有 raw 六字节直接按同一个整数做过度解释。

### USB, RAID And Virtual Devices

- bridge 不支持透传：`Unsupported`，说明 bridge/controller 原因。
- RAID 暴露逻辑盘但不暴露成员盘：显示逻辑设备身份，不声明成员盘健康。
- 虚拟磁盘：显示 `Virtual`，SMART 健康通常为 `Unsupported`。
- `MSFT_PhysicalDisk.HealthStatus` 可以作为 Storage Spaces 来源展示，但不能伪装成设备原生 SMART。

## Protocol Health Table

### NVMe Table

列：

```text
状态 | ID | 属性 | 解码值 | 单位 | 原始值 | 来源
```

建议行：

- Critical Warning。
- Composite Temperature。
- Available Spare。
- Available Spare Threshold。
- Percentage Used。
- Data Units Read。
- Data Units Written。
- Host Read Commands。
- Host Write Commands。
- Controller Busy Time。
- Power Cycles。
- Power On Hours。
- Unsafe Shutdowns。
- Media and Data Integrity Errors。
- Error Information Log Entries。
- Warning/Critical Temperature Time。
- Temperature Sensors 1-8，设备提供时展示。

### ATA SMART Table

列：

```text
状态 | ID | 属性 | 当前 | 最差 | 阈值 | 类型 | 更新方式 | 原始值 | 解码值
```

表格要求：

- 保留两位十六进制 ID。
- 原始 bytes 使用固定宽度等宽字体。
- 支持按 `全部 / 异常 / 关键` 过滤。
- 默认按设备返回顺序或 attribute ID 排序。
- 支持复制选中行。
- tooltip 解释 normalized、threshold 和 raw 的区别。
- 行状态使用图标、文本和语义色，不仅使用彩色圆点。
- 大列表启用 WPF virtualization。

### Empty And Unsupported States

表格区域不能简单空白，应显示明确状态：

```text
该设备身份信息可用，但控制器未提供 SMART/NVMe Health 透传。
```

并展示：

- 设备总线和控制器。
- 尝试的数据源。
- 简短错误分类。
- 是否可以以管理员权限重试。
- 复制诊断信息入口。

## Volumes And Partitions

建议在 SMART 表之后增加卷/分区 section。

字段：

- Partition number。
- Partition style：GPT/MBR/RAW。
- Partition type / GPT type。
- Offset。
- Capacity。
- Volume label。
- File system。
- Drive letter / mount point。
- Free space。
- BitLocker 状态，后续可选。
- System / Boot / PageFile / CrashDump / Recovery 角色，能可靠读取时展示。

卷信息来自 Windows 存储和 WMI/CIM，不属于 SMART。来源 badge 应单独标记。

## Visual Design

### Design Principles

- 延续 CPU/内存详情页的 24 px 页面标题、13 px 字段、7 px radius 和动态主题 token。
- 保留 CrystalDiskInfo 的信息密度和单盘诊断逻辑，不复刻蓝色渐变和球形温度按钮。
- 页面首先是工作型诊断工具，不使用营销式 hero、插画或大面积装饰。
- 健康和温度是首屏强信号，但不能挤压身份、链路和异常原因。
- 不在 card 内继续嵌套 card。设备 tile 是重复项，可以使用卡片；页面 section 保持与现有详情页一致。
- 所有固定格式表格和 tile 使用稳定尺寸，加载/错误状态不能引发布局跳动。

### Desktop Layout

```text
-------------------------------------------------------------------
存储设备                         3 个物理设备  [刷新] [复制] [保存]

[Disk 0 NVMe 良好 49 C C:] [Disk 1 SATA 注意 41 C D:] [Disk 2 USB]

PM9F1 SED Samsung 2048GB
Disk 0 · 2.05 TB · NVMe SSD · C:\

| 健康状态                 | 设备身份              | 使用统计          |
| 良好                     | 固件 77320459         | Host Reads 155 TB |
| 预计剩余寿命 97%         | 序列号 ...            | Host Writes 103 TB|
| 温度 49 C                | NVMe / PCIe 4.0 x4   | 4898 小时 / 15201 |

| 接口与功能                                                       |
| NVMe 2.0 · PCIe 4.0 x4 / max 4.0 x4 · SMART · TRIM · VWC       |

| SMART / Health                                      [全部|异常|关键]|
| 状态 ID 属性                  解码值       单位       原始值        |
| ...                                                               |

| 卷与分区                                                          |
| C:  NTFS  1.86 TiB  可用 820 GiB  System / Boot                  |

| 数据说明                                                          |
-------------------------------------------------------------------
```

### Adaptive Layout

主窗口最小宽度下，页面可用空间会受到 236 px 左侧导航影响。

建议行为：

- 内容宽度 >= 960 px：健康、身份、统计三列。
- 内容宽度 720-959 px：健康块占左侧，身份/统计在右侧上下排列。
- 内容宽度 < 720 px：全部单列堆叠，SMART 表水平滚动。
- 设备 tile 自动换行。
- 表格保持最小列宽，不通过缩小字体塞入所有列。

WPF 实现时可使用自定义 adaptive panel、窗口宽度触发的列定义切换或两个明确布局模板。不要依赖 viewport 比例缩放字体。

### Source Badges

沿用 CPU/内存字段来源模式：

```text
WMI
Storage API
NVMe
ATA SMART
SCSI
Storage Spaces
推导
待接入
```

推导值追加 `*`，tooltip 给出计算方式。

### Semantic Status Tokens

主题 JSON 后续需要新增语义 token，而不是在页面里硬编码颜色：

```text
HwScopeStatusGoodColor / Brush
HwScopeStatusCautionColor / Brush
HwScopeStatusCriticalColor / Brush
HwScopeStatusUnknownColor / Brush
HwScopeStatusInfoColor / Brush
```

浅色和深色主题都要验证文字对比度。状态同时使用 icon 和文本，保证色觉差异用户可读。

## Interaction Design

### Navigation

把主窗口导航改为：

```xml
<ui:NavigationViewItem Content="存储设备"
                       Tag="storage-detail"
                       Icon="{ui:SymbolIcon HardDrive20}"
                       Click="NavigationItem_Click" />
```

`MainWindow` 持有 `StorageDetailPage` 单例，与 CPU/内存页一致。

### Initial Load

推荐流程：

```text
Open page
  -> consume preloaded disk inventory immediately
  -> render physical disk selector and WMI identity
  -> choose system disk, otherwise first physical disk
  -> asynchronously query selected disk protocol/health
  -> merge and render health/attributes
```

不要因为 SMART provider 慢或失败而阻止页面显示基础身份和卷信息。

### Device Selection

- 默认优先选中承载系统卷的物理设备。
- 无法确定系统盘时选中第一块稳定排序的物理设备。
- 切换设备时取消等待旧请求或使用 version guard 忽略旧结果。
- 已加载设备可保留短期会话缓存，切回时立即显示旧值并在用户要求时刷新。
- inventory 刷新后按稳定设备 ID 恢复选择。

### Refresh Semantics

存储健康是动态数据，不应把它长期塞进 `HardwareInventorySnapshot`。

推荐拆分：

- `HardwareInventorySnapshot`：物理设备基础身份和列表。
- `StorageHealthSnapshot`：按设备、按时间生成的动态协议数据。

页面 Refresh：

- 刷新当前选中设备的 identity/link/health/volume mapping。
- 必要时同步最新 disk inventory，但不要为了温度刷新而重跑全部 CPU、内存、显示器 WMI。
- 保留旧值直到新值成功返回。
- 失败时保留旧值并显示“上次成功时间”和本次错误。

这将成为现有“所有页面刷新都刷新全局 inventory”规则的明确例外，因为 SMART/temperature 属于动态、设备级数据。

### Copy And Save

文本报告结构：

```text
Storage Device

Identity
Model: PM9F1 SED Samsung 2048GB [Storage API]
Firmware: 77320459 [NVMe]
Serial: ... [NVMe]
Capacity: ...

Interface
Bus: NVMe
Current Link: PCIe 4.0 x4
Maximum Link: PCIe 4.0 x4

Health
Status: Good
Temperature: 49 C
Remaining Life: 97% [NVMe Percentage Used]

Usage
Host Reads: ...
Host Writes: ...
Power On Hours: ...

Protocol Attributes
...

Volumes
...

Notes
...

Generated At: yyyy-MM-dd HH:mm:ss
```

默认报告只包含当前选中设备。未来可以增加“全部设备报告”。

## Data Model Proposal

新增 Core 领域：

```text
src/HwScope.Core/Hardware/Storage/
  StorageDataSource.cs
  StorageFieldValue.cs
  StorageDetailReport.cs
  StorageHealthStatus.cs
  StorageProtocolKind.cs
  StorageDetailCollector.cs
  StorageDetailReportFormatter.cs
  IStorageDetailProvider.cs
  WindowsStorageDetailProvider.cs
  NvmeHealthParser.cs
  AtaSmartParser.cs
  StorageHealthEvaluator.cs
```

Windows/PInvoke 边界：

```text
src/HwScope.Core/Windows/Storage/
  StorageDeviceHandle.cs
  StorageDeviceIoControl.cs
  StoragePropertyQuery.cs
  StorageProtocolQuery.cs
  StorageNativeModels.cs
```

与 CPU topology 一样，unsafe buffer parsing 和 P/Invoke 留在 `HwScope.Core.Windows`，业务层只消费 typed records。

### Field Wrapper

```csharp
public sealed record StorageFieldValue<T>(
    T? Value,
    string DisplayText,
    StorageDataSource Source,
    bool IsAvailable,
    bool IsEstimated = false,
    string? Note = null);
```

### Report Shape

```csharp
public sealed record StorageDetailReport(
    StorageDeviceIdentity Identity,
    StorageInterfaceInfo Interface,
    StorageHealthSummary Health,
    StorageLifetimeStatistics Lifetime,
    IReadOnlyList<StorageProtocolAttribute> Attributes,
    IReadOnlyList<StoragePartitionInfo> Partitions,
    IReadOnlyList<StorageVolumeInfo> Volumes,
    IReadOnlyList<StorageDataNote> Notes,
    DateTimeOffset GeneratedAt);
```

核心结构：

```csharp
public sealed record StorageDeviceIdentity(
    string StableId,
    int? PhysicalDriveNumber,
    StorageFieldValue<string> Model,
    StorageFieldValue<string> Firmware,
    StorageFieldValue<string> SerialNumber,
    StorageFieldValue<string> DevicePath,
    StorageFieldValue<string> DeviceInstanceId,
    StorageFieldValue<ulong> Capacity,
    StorageFieldValue<string> MediaType,
    StorageProtocolKind Protocol);

public sealed record StorageHealthSummary(
    StorageHealthStatus Status,
    string StatusReason,
    StorageFieldValue<double> TemperatureCelsius,
    StorageFieldValue<int> RemainingLifePercent,
    IReadOnlyList<StorageHealthFlag> Flags);

public sealed record StorageProtocolAttribute(
    string Id,
    string Name,
    StorageAttributeSeverity Severity,
    string DisplayValue,
    string? Unit,
    string RawValue,
    int? Current,
    int? Worst,
    int? Threshold,
    StorageDataSource Source,
    string? Note);
```

不要让 UI 自己解析 raw bytes、NVMe data units、Kelvin 或 ATA normalized values。

## Provider Architecture

建议采用 provider 聚合，而不是一个包含所有协议分支的巨型 collector：

```text
StorageDetailCollector
  -> baseline inventory and volume mapper
  -> Windows storage property provider
  -> protocol detector
       -> NVMe provider
       -> ATA SMART provider
       -> SCSI/SAS provider, future
       -> Storage Spaces provider, future
  -> health evaluator
  -> immutable report
```

接口示例：

```csharp
public interface IStorageDetailProvider
{
    bool CanHandle(StorageDeviceDescriptor device);

    Task<StorageProviderResult> QueryAsync(
        StorageDeviceDescriptor device,
        CancellationToken cancellationToken);
}
```

Provider 失败是设备级失败，不应导致其他设备或基础 inventory 不可用。

## Preload And Dynamic Data

### Suitable For HardwareInventorySnapshot

- Physical drive number。
- Model。
- Size。
- Media/bus type。
- Device ID / PnP ID。
- Firmware/serial，读取成本低且不需要协议命令时。
- 基础 partition/volume mapping，视启动性能评估决定。

### On-Demand Only

- SMART/NVMe Health log。
- Temperature。
- Remaining life。
- Host reads/writes。
- Error counters。
- Self-test state。
- 可能唤醒休眠 HDD 的查询。

启动预加载不应因为某个 USB bridge 的 SMART 查询超时而延迟主窗口。

## Safety And Access Boundaries

- 第一版只使用 Windows 文档化、只读的 storage query/health 路径。
- 设备句柄使用满足查询所需的最小访问权限，权限不足时可以安全降级。
- 所有 buffer 长度、offset、protocol payload length 和返回版本必须校验后再解析。
- 不接受设备返回的字符串或长度直接驱动无限分配。
- 每设备查询需要超时、取消和 stale-result guard。
- 如果标准 API 在部分驱动上可能长期阻塞，应考虑把 protocol query 移入独立 worker 进程，复用内存 benchmark 的进程隔离和 process-tree cancellation 模式。
- 不在 UI 线程调用 `DeviceIoControl` 或 WMI。
- 不实现任意 ATA command、任意 NVMe admin command 或用户提供 raw opcode。
- 不记录完整序列号到默认 crash log；导出报告时后续可提供脱敏选项。

## Error Model

错误分类建议：

```text
AccessDenied
DeviceNotFound
DeviceRemoved
UnsupportedBus
ProtocolPassThroughUnavailable
ControllerBlocked
MalformedResponse
Timeout
Cancelled
DriverError
Unknown
```

UI 显示简短、可操作的中文说明；完整 Win32 error、IOCTL、device path 和 provider stack 写入受控诊断对象，不直接用 MessageBox 展示整段 exception。

热拔插行为：

- 当前设备移除后保留最后报告并标记 `设备已断开`。
- 重新枚举磁盘列表。
- 自动选择仍存在的设备，但不把另一个磁盘的数据显示在旧标题下。

当前实现已支持重新枚举和自动选择仍存在设备，但尚未保留已移除设备的最后报告或显示独立的 `设备已断开` 状态。

## Performance And Polling

- 页面首次显示基础 inventory 应接近即时。
- 选中设备 health 查询目标在 1 秒内完成；超过阈值显示正在读取，但不冻结页面。
- 当前 UI 等待采用 5 秒 soft timeout；超时后恢复交互，但同步 IOCTL 可能继续在后台返回。
- 第一版不自动轮询。
- 后续实时模式最短间隔建议 10-30 秒，并默认关闭。
- 对可能休眠的 HDD，轮询前应尊重“不唤醒休眠磁盘”设置。
- 切换设备时优先取消或忽略旧查询结果。

## Accessibility And Usability

- 状态不能只用绿/黄/红颜色区分。
- 表格支持键盘焦点和行复制。
- tooltip 不承载唯一重要信息；异常原因必须在可见文本中出现。
- 等宽 raw value 仍需满足主题对比度。
- 长型号、序列号和 device path 使用 wrapping 或可复制省略，不能撑破布局。
- 温度、容量和累计计数使用 invariant、可预测格式。

## Implementation Plan

### Milestone 1: Inventory Enrichment And Page Shell

- 扩展 `DiskDriveSnapshot`。
- 建立物理磁盘、分区和卷映射。
- 新增 Storage Core 模型、formatter 和 `StorageDetailPage`。
- 接入 `storage-detail` 导航。
- 完成设备 selector、基础身份、物理分区/卷列表、复制和保存。
- 未读取 health 时明确显示不可用。

### Milestone 2: Windows Storage Properties

- 增加只读 storage property query。
- 获取标准身份、bus、sector、capability 和可用链路字段。
- 增加来源 badge 和设备级错误状态。

### Milestone 3: NVMe Health

- 读取和解析 NVMe Identify/Health log。
- 展示 temperature、spare、percentage used、data units、power/error counters。
- 实现 NVMe health evaluator。
- 加入 raw fixture parser tests。

### Milestone 4: ATA SMART

- 读取 ATA overall status、attributes 和 thresholds。
- 展示 ATA 专用表格列。
- 实现保守的关键属性判定。
- 保留 vendor raw bytes。

### Milestone 5: Coverage And Diagnostics

- USB/SAT 支持矩阵。
- Storage Spaces/RAID/virtual 状态。
- 超时和隔离策略。
- 完善设备移除、权限不足和 malformed response 验证。

## Testing Strategy

设计建立时仓库没有测试工程。当前已经新增：

```text
src/HwScope.Core.Tests/
```

截至 2026-07-14，共有 33 项 Core tests，已覆盖 descriptor/NVMe/ATA parser、NVMe protocol payload、ATA overall signature/checksum、健康判定、soft timeout、字段来源合并、bus formatting 和 formatter。以下仍是完整测试目标：

- NVMe Health log 正常样本。
- Critical Warning 各 bit。
- Percentage Used 为 0、99、100、超过 100。
- Kelvin/Celsius 转换和无效温度。
- 128-bit NVMe counters 的边界和显示。
- ATA attribute/threshold 合并。
- ATA raw byte order。
- full/short/malformed buffer。
- USB/RAID unsupported result。
- stable disk ID 和 volume mapping。
- health evaluator 的 Good/Caution/Critical/Unknown。

当前仍缺 USB/RAID/SAT 实机与 fixture 矩阵、`StorageVolumeMapper` 独立单元测试、设备热拔插自动化测试和 WPF 页面级自动化测试；现有分区显示与页面重入使用本机 UI Automation 做过工作流验证。

不得用真实系统盘写入测试。硬件集成测试只允许只读查询，并与脱敏 fixture 分离。

## Manual Validation Matrix

至少验证：

```text
NVMe system SSD
SATA SSD
SATA HDD
USB-SATA bridge
USB-NVMe bridge
Storage Spaces or RAID, if available
VHD/VHDX or virtual machine disk
Standard user and administrator modes
Light and dark themes
Single disk and multi-disk systems
```

每类设备检查：

- 设备列表没有重复或错误合并。
- 型号、容量、序列号和卷映射正确。
- unsupported 与 unhealthy 不混淆。
- 刷新不冻结 UI。
- 热拔插不会显示错盘数据。
- Copy/Save 与 UI 值一致。
- 原始值和解码值可追溯。

## Acceptance Criteria

### Page Foundation

- `硬件 -> 存储设备` 打开独立详情页，不再路由摘要。
- 页面以物理设备为选择单位，并能显示关联卷。
- 页面首屏包含设备选择、身份、接口、健康状态和温度位置。
- 无 health provider 时显示明确不可用状态，不默认“良好”。
- Refresh/Copy/Save 可用，异步刷新不冻结主窗口。
- 字段带来源和缺失说明。

### NVMe

- 支持的 NVMe 设备展示标准 Health log 关键字段。
- 温度、percentage used、spare、读写量、通电和错误计数来自真实 payload。
- 健康原因可见，原始值可复制。
- 无效/截断 payload 不会越界或导致应用崩溃。

### ATA

- 支持的 ATA 设备展示 attribute 和 threshold 表。
- raw bytes、normalized value 和 threshold 不混淆。
- 不使用无依据的通用寿命百分比。
- bridge 不支持透传时显示 Unsupported，而不是 Critical。

## Risks And Mitigations

### Device Identity Mismatch

风险：WMI、Storage API 和 volume mapping 使用不同标识，可能把 health 合并到错误磁盘。

缓解：使用 physical drive number、device path、instance ID、serial 和 protocol identifier 多字段匹配；不确定时拒绝合并并显示诊断。

### Vendor-Specific SMART Semantics

风险：相同 ATA attribute ID 的 raw bytes 在不同厂商上语义不同。

缓解：保留 raw；只解码有可靠规则的字段；厂商规则必须版本化并带来源。

### Blocking Drivers

风险：某些 USB/RAID 驱动的 IOCTL 可能长期阻塞。

缓解：后台执行、设备级超时、stale-result guard；必要时使用可终止的 worker 进程隔离。

### False Health Confidence

风险：一个醒目的绿色百分比会让用户忽略 warning bits 或把估算当事实。

缓解：状态显示触发原因；百分比只用于标准化程度足够的字段；ATA 默认不显示百分比。

### HDD Wake-Up

风险：自动健康查询会唤醒休眠 HDD，增加噪声和功耗。

缓解：第一版不轮询；后续增加“不唤醒休眠磁盘”策略和显式刷新。

## Definition Of Done For Initial Storage Detail Workstream

初始工作流完成的标准：

- 独立存储详情页、导航和领域模型已经建立。
- 基础 inventory 与动态 health 明确分层。
- 物理磁盘、分区和卷映射正确。
- 至少 NVMe 标准 Health log 在支持的 Windows/NVMe 设备上真实可读。
- 页面不会把不支持、权限不足或 bridge 阻塞显示成磁盘故障。
- 健康判定和关键 parser 有 fixture tests。
- 页面在浅色/深色、多盘、窄窗口和长字段场景下无重叠。
- Copy/Save 输出包含来源、原始值、解码值、生成时间和诊断说明。
