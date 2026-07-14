# Storage Detail Page Implementation Plan

本文档是 `storage-detail-page-design.md` 的工程落地方案。它把存储详情页拆成可提交、可验证的开发阶段，并明确现有 HwScope 模块需要修改的文件、Windows API 边界、协议 parser、页面状态、测试夹具和验收方式。

本方案的首个完整目标不是“做一个存储页面占位”，而是交付：

- 独立物理磁盘详情页。
- 正确的磁盘、分区和卷映射。
- 标准 Windows storage property 读取。
- 至少 NVMe SMART / Health Information log 的真实读取与展示。
- 保守、可解释的健康判定。
- 明确的 Unsupported/Unknown/AccessDenied 状态。
- 可测试的二进制 parser。

ATA SMART 首个实现已经落地；USB bridge、RAID 和 Storage Spaces 继续在相同架构下推进，但不能用伪造数据扩展已验证能力边界。

## Current Implementation Status

截至 2026-07-14，已完成：

- Milestone 0-10 的首个 NVMe 页面交付。
- enriched inventory、Storage management volume mapping、domain model、provider aggregation 和 Windows native query layer。
- NVMe Health log 真实读取、parser、health evaluator、UI、CLI、copy/save 和主题状态 token。
- Milestone 11 的 ATA SMART RETURN STATUS、parser、threshold merge、legacy SMART/ATA pass-through provider 和 fixture tests。
- Milestone 12 CLI diagnostic entry。
- P1/P2 收敛：ATA malformed/overall 保守判定、跨设备隔离与 5 秒 soft timeout、物理分区 UI、页面重入恢复、字段级来源和 `storage list` 真实 bus。

验证状态：

- `dotnet build`：通过，0 warning / 0 error。
- `dotnet test`：33 tests passed。
- Samsung PM9F1 NVMe：真实读取成功，identity、temperature、remaining life、128-bit counters、partitions/volume 已交叉显示。
- WPF 页面：使用非管理员开发通道完成首屏、SMART table、3 个 GPT 分区显示，以及快速切走再返回后的缓存恢复验证。
- ATA：代码和合成测试完成，真实 SATA SSD/HDD 验证待补。
- USB/RAID/Storage Spaces：当前按 standard provider best effort 和 Unsupported/Unknown 降级，广覆盖仍待后续硬件矩阵。

## Implementation Starting Point (Historical)

本方案建立时，解决方案只有以下项目：

```text
HwScope.sln
  HwScope.App
  HwScope.Cli
  HwScope.Core
```

native memory benchmark 项目通过 CMake 独立存在，不在 `.sln` 中；当时仓库还没有测试项目。当前解决方案已经包含 `HwScope.Core.Tests`，当前能力和验证结果以上方 `Current Implementation Status` 为准。

当时的存储模型只有：

```csharp
public sealed record DiskDriveSnapshot(
    string Model,
    ulong Size,
    string MediaType,
    string InterfaceType);
```

采集查询：

```sql
SELECT Model, Size, MediaType, InterfaceType
FROM Win32_DiskDrive
```

当时 `硬件 -> 存储设备` 的 navigation tag 是 `summary`。以上内容仅用于记录方案起点，不描述当前代码。

## Locked Engineering Decisions

### Keep Static Inventory And Dynamic Health Separate

`HardwareInventorySnapshot` 只保存枚举和低成本、相对稳定的设备身份。

SMART、NVMe Health、温度、寿命、错误计数属于按设备读取的动态快照：

```text
HardwareInventorySnapshot
  DiskDriveSnapshot[]
    stable baseline identity

StorageDetailService
  StorageDetailReport by stable device ID
    dynamic protocol and health data
```

存储页的刷新不调用全局 `HardwarePreload.RefreshAsync()`，除非用户或系统明确要求重新枚举设备。页面首次加载仍消费 preload inventory。

### Managed Windows API First

第一阶段使用 C# P/Invoke + `SafeFileHandle` 调用 Windows 文档化的只读 storage API：

- `CreateFileW`。
- `DeviceIoControl`。
- `IOCTL_STORAGE_QUERY_PROPERTY`。
- NVMe protocol-specific property query。

理由：

- 当前 Core 已允许 unsafe code。
- 现有 `LogicalProcessorInformation` 已建立“P/Invoke 留在 Core.Windows”的边界。
- 标准 property query 不需要额外 native 项目和 JSON protocol。
- parser 可以在纯 managed 测试中验证。

不在第一阶段创建 `HwScope.Native.Storage`。如果 USB/RAID driver 出现不可接受的阻塞行为，再把 protocol query 移入进程 worker；领域模型和 parser 不因此改变。

### Read-Only Commands Only

允许：

- 查询 descriptor。
- 查询 alignment/capability。
- 读取 NVMe identify 和 health log。
- 读取 ATA SMART attributes/threshold/status。

禁止：

- 任意用户输入 opcode。
- 写缓存切换。
- firmware download/activate。
- format、sanitize、secure erase。
- namespace create/delete。
- 自动 self-test。

### Protocol-Specific Tables

NVMe 和 ATA 不共享固定列模板。

- Core 使用统一 `StorageProtocolAttribute` 语义模型。
- UI 根据 `StorageProtocolKind` 选择 NVMe 或 ATA 列集合。
- 原始值始终保留。

### No Generic ATA Life Percentage

- NVMe 可以从标准 `Percentage Used` 得到显示用剩余寿命。
- ATA 只有在明确、经过测试的厂商规则存在时才显示寿命百分比。
- 第一版 ATA 默认只显示状态和属性，不显示百分比。

## Target Project Layout

### HwScope.Core

```text
src/HwScope.Core/Hardware/Storage/
  StorageDataSource.cs
  StorageFieldValue.cs
  StorageProtocolKind.cs
  StorageHealthStatus.cs
  StorageError.cs
  StorageDeviceDescriptor.cs
  StorageDetailReport.cs
  StorageDetailCollector.cs
  StorageDetailReportFormatter.cs
  StorageHealthEvaluator.cs
  StorageAttributeCatalog.cs
  StorageVolumeMapper.cs
  Providers/
    IStorageDetailProvider.cs
    StorageProviderResult.cs
    WindowsStoragePropertyProvider.cs
    NvmeStorageProvider.cs
    AtaSmartStorageProvider.cs

src/HwScope.Core/Hardware/Storage/Nvme/
  NvmeIdentifyController.cs
  NvmeSmartHealthLog.cs
  NvmeSmartHealthParser.cs
  NvmeValueFormatter.cs

src/HwScope.Core/Hardware/Storage/Ata/
  AtaSmartAttribute.cs
  AtaSmartThreshold.cs
  AtaSmartParser.cs
  AtaSmartValueFormatter.cs

src/HwScope.Core/Windows/Storage/
  StorageDeviceHandle.cs
  StorageDeviceIoControl.cs
  StorageNativeConstants.cs
  StorageNativeModels.cs
  StoragePropertyQuery.cs
  NvmeProtocolQuery.cs
  AtaSmartQuery.cs
```

### HwScope.App

```text
src/HwScope.App/Services/
  StorageDetailService.cs

src/HwScope.App/Pages/
  StorageDetailPage.xaml
  StorageDetailPage.xaml.cs

src/HwScope.App/Pages/Storage/
  StoragePageViewModels.cs
  StoragePageConverters.cs
```

不引入 MVVM framework。页面继续采用当前 CPU/内存页的轻量 view record + code-behind 模式。

### Tests

```text
src/HwScope.Core.Tests/
  HwScope.Core.Tests.csproj
  Hardware/Storage/
    StorageHealthEvaluatorTests.cs
    StorageVolumeMapperTests.cs
    NvmeSmartHealthParserTests.cs
    AtaSmartParserTests.cs
    StorageDetailReportFormatterTests.cs
  Fixtures/Storage/
    nvme-health-normal.bin
    nvme-health-warning.bin
    nvme-health-truncated.bin
    ata-smart-data.bin
    ata-smart-thresholds.bin
    README.md
```

fixtures 必须是合成或脱敏样本，不包含真实序列号。

## End-To-End Data Flow

```text
App startup
  -> HardwareInventoryCollector
  -> enriched DiskDriveSnapshot[]
  -> HardwarePreloadService.Current

Open StorageDetailPage
  -> show disk tiles from DiskDriveSnapshot[]
  -> choose system disk or first disk
  -> StorageDetailService.EnsureLoadedAsync(stableId)
  -> StorageDetailCollector.CollectAsync(device)
       -> StorageVolumeMapper
       -> WindowsStoragePropertyProvider
       -> protocol provider selected by detected bus/protocol
            -> NvmeStorageProvider
            -> AtaSmartStorageProvider
       -> StorageHealthEvaluator
       -> StorageDetailReportFormatter-ready report
  -> UI render
```

错误原则：

- baseline 成功、protocol 失败：仍返回可渲染 report。
- volume mapping 失败：仍显示设备 identity/health。
- 单个磁盘失败：不影响其他磁盘。
- payload malformed：provider 返回 `MalformedResponse`，不抛到 WPF Dispatcher。

## Milestone 0: Test Foundation

存储 parser 涉及不受信任的 driver payload，测试工程必须在 parser 实现前建立。

### Add Test Project

创建：

```text
src/HwScope.Core.Tests/HwScope.Core.Tests.csproj
```

要求：

- `net8.0-windows`。
- 引用 `HwScope.Core`。
- 使用仓库当前兼容的 xUnit + `Microsoft.NET.Test.Sdk`。
- 加入 `HwScope.sln` 的 `src` solution folder。
- 测试不要求管理员权限。
- 默认测试不打开真实 `PhysicalDrive`。

Core 内部 parser 可以通过以下方式测试：

```csharp
[assembly: InternalsVisibleTo("HwScope.Core.Tests")]
```

在 Core 新增 `Properties/AssemblyInfo.cs`，不要为了测试把 Windows native internals 全部改成 public。

### Initial Tests

先建立失败测试：

- NVMe buffer 小于 512 bytes 返回 structured parse error。
- 512-byte zero buffer 可以解析，但温度/计数按规则不可用或为零。
- unknown protocol 不选择 NVMe/ATA provider。
- health evaluator 对 unavailable data 返回 Unknown/Unsupported。

### Validation

```powershell
dotnet test .\src\HwScope.Core.Tests\HwScope.Core.Tests.csproj
dotnet build
```

## Milestone 1: Enrich Disk Inventory

### Extend DiskDriveSnapshot

修改：

```text
src/HwScope.Core/Hardware/Inventory/HardwareInventorySnapshot.cs
```

建议模型：

```csharp
public sealed record DiskDriveSnapshot(
    int? Index,
    string DeviceId,
    string PnpDeviceId,
    string Model,
    string FirmwareRevision,
    string SerialNumber,
    ulong Size,
    string MediaType,
    string InterfaceType,
    uint? BytesPerSector,
    int? PartitionCount,
    int? ScsiBus,
    int? ScsiPort,
    int? ScsiTargetId,
    int? ScsiLogicalUnit);
```

必须使用 nullable number。磁盘编号、SCSI bus/port/target/lun 的 `0` 是合法值，不能继续使用“0 表示缺失”的 helper 语义。

### Extend Wmi Helpers

修改：

```text
src/HwScope.Core/Windows/Wmi.cs
```

新增：

```csharp
public static int? GetNullableInt(ManagementBaseObject? obj, string propertyName);
public static uint? GetNullableUInt(ManagementBaseObject? obj, string propertyName);
public static bool? GetNullableBool(ManagementBaseObject? obj, string propertyName);
```

要求：

- 接受数值 0。
- 检查 signed/unsigned overflow。
- 缺失、`DBNull`、不支持属性时返回 null。
- 不使用字符串 `0` 与缺失混淆。

### Update WMI Query

修改：

```text
src/HwScope.Core/Hardware/Inventory/HardwareInventoryCollector.cs
```

查询：

```sql
SELECT Index, DeviceID, PNPDeviceID, Model, FirmwareRevision,
       SerialNumber, Size, MediaType, InterfaceType, BytesPerSector,
       Partitions, SCSIBus, SCSIPort, SCSITargetId, SCSILogicalUnit
FROM Win32_DiskDrive
```

转换后立即清理字符串中的 NUL 和多余空格，但保留原始 device path 的必要反斜杠。

### Stable Device ID

新增：

```text
StorageDeviceDescriptor.StableId
```

生成优先级：

1. normalized PnP device instance ID。
2. normalized DeviceID + serial。
3. physical drive index + model + size。

Stable ID 只用于当前机器和当前枚举会话的选择恢复，不承诺成为跨系统资产 ID。

不得只使用 serial：

- 部分 USB bridge 返回相同或空 serial。
- 部分虚拟磁盘序列号不稳定。
- Storage Spaces 可能暴露逻辑 identity。

### Compatibility Updates

检查并更新：

```text
src/HwScope.Core/Hardware/HardwareCollector.cs
src/HwScope.Core/Hardware/Cpu/CpuDetailCollector.cs
src/HwScope.Cli/Program.cs
```

摘要仍只使用 `Model` 和 `Size`，输出语义不变。

### Tests

- Disk 0 被保留为 `Index = 0`。
- 缺失 index 为 null。
- stable ID 对空 serial 有 deterministic fallback。
- 相同 model/size、不同 PnP ID 不发生合并。

## Milestone 2: Storage Management Inventory And Volume Mapping

### Why This Is On-Demand

`root/Microsoft/Windows/Storage` 查询可能比当前 `Win32_DiskDrive` baseline 更慢，并且页面需要比摘要更丰富的 partition/volume 数据。第一版先在打开存储页时查询，不加入启动 11-step preload。

### Models

新增：

```csharp
internal sealed record WindowsManagedDisk(...);
public sealed record StoragePartitionInfo(...);
public sealed record StorageVolumeInfo(...);
public sealed record StorageDeviceRole(
    bool IsSystem,
    bool IsBoot,
    bool HostsPageFile,
    bool HostsCrashDump,
    bool IsReadOnly,
    bool IsOffline);
```

### Queries

scope：

```text
root\Microsoft\Windows\Storage
```

读取：

```text
MSFT_Disk
MSFT_Partition
MSFT_Volume
```

不要使用字符串拼接的 ASSOCIATORS 查询做主要路径。分别读取纯 record，再通过 `DiskNumber`、`PartitionNumber`、`DriveLetter` 和 normalized access paths 合并，便于测试。

### Mapping Algorithm

1. 用 `MSFT_Disk.Number` 对应 `DiskDriveSnapshot.Index`。
2. `MSFT_Partition.DiskNumber` 归属物理盘。
3. partition 的 `DriveLetter` 和 `AccessPaths` 建立 mount point 集合。
4. 用 normalized `MSFT_Volume.Path`、DriveLetter 和 access path 关联 volume。
5. 没有盘符的 volume 保留 GUID path。
6. 无法一对一映射时记录 ambiguity note，不把 volume 分配给猜测的 disk。

### Fallback

在 storage namespace 不可用时，可使用 `root\cimv2`：

```text
Win32_DiskPartition
Win32_LogicalDisk
Win32_DiskDriveToDiskPartition
Win32_LogicalDiskToPartition
```

fallback 需要独立 mapper，最终输出相同 typed records。

### Error Visibility

当前 `Wmi.Query()` 会吞掉常见 WMI 异常并返回空集合。存储详情需要区分：

- 查询成功但确实为空。
- namespace/class 不存在。
- 权限不足。
- provider failure。

不要改变现有 `Wmi.Query()` 的全局行为。为存储模块增加带结果状态的 helper：

```csharp
internal sealed record WmiQueryResult<T>(
    IReadOnlyList<T> Items,
    StorageError? Error);
```

### Tests

- 多分区单盘。
- 无盘符 recovery partition。
- 一个 volume 多 access path。
- Disk 0/Disk 1 不串盘。
- ambiguity 不静默分配。
- storage namespace unavailable fallback。

## Milestone 3: Core Storage Domain Models

### Data Source

```csharp
public enum StorageDataSource
{
    Unknown,
    Wmi,
    WindowsStorage,
    StorageApi,
    Nvme,
    AtaSmart,
    Scsi,
    StorageSpaces,
    Computed,
    Placeholder
}
```

### Protocol And Health

```csharp
public enum StorageProtocolKind
{
    Unknown,
    Nvme,
    Ata,
    Scsi,
    Sas,
    Usb,
    Raid,
    Virtual,
    StorageSpaces
}

public enum StorageHealthStatus
{
    Good,
    Caution,
    Critical,
    Unknown,
    Unsupported
}
```

`Usb` 是 transport，不一定是底层 protocol。模型额外保留：

```csharp
StorageProtocolKind Protocol
StorageBusKind Bus
StorageProtocolKind? BridgedProtocol
```

避免把 USB-NVMe 错误简化成只有 USB。

### Field Wrapper

实现与 CPU/Memory 同构的 `StorageFieldValue<T>`：

```csharp
public sealed record StorageFieldValue<T>(
    T? Value,
    string DisplayText,
    StorageDataSource Source,
    bool IsAvailable,
    bool IsEstimated = false,
    string? Note = null);
```

helper 覆盖：

- Text。
- Number。
- BytesBinary。
- BytesDecimal。
- Temperature。
- DurationHours。
- Percentage。
- Counter。
- Placeholder。

容量格式要求：

- 厂商容量：十进制 GB/TB。
- Windows usable capacity：二进制 GiB/TiB。
- report 保留 exact bytes。

### Report

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
    StorageCollectionDiagnostics Diagnostics,
    DateTimeOffset GeneratedAt);
```

`StorageCollectionDiagnostics` 包含每个 provider：

```text
Provider name
Status
Elapsed
Error category
Win32 error
Short message
Full diagnostic text, non-UI
```

### Error Model

```csharp
public sealed record StorageError(
    StorageErrorKind Kind,
    string Message,
    int? NativeErrorCode = null,
    string? Diagnostic = null);
```

枚举：

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

UI 只显示 `Message`；formatter/diagnostics 可选择包含更多信息，但默认不输出完整敏感 device path。

## Milestone 4: StorageDetailCollector And Provider Aggregation

### Public API

```csharp
public sealed class StorageDetailCollector
{
    public Task<StorageDetailReport> CollectAsync(
        StorageDeviceDescriptor device,
        CancellationToken cancellationToken = default);
}
```

构造函数注入：

```csharp
StorageVolumeMapper
IReadOnlyList<IStorageDetailProvider>
StorageHealthEvaluator
TimeProvider
```

不要在 collector 内直接 `new` 每个 provider，测试需要替换 provider。

### Provider Result

```csharp
public sealed record StorageProviderResult(
    string ProviderName,
    bool Supported,
    StorageDeviceIdentityPatch? Identity,
    StorageInterfaceInfoPatch? Interface,
    StorageHealthData? Health,
    StorageLifetimeData? Lifetime,
    IReadOnlyList<StorageProtocolAttribute> Attributes,
    IReadOnlyList<StorageDataNote> Notes,
    StorageError? Error,
    TimeSpan Elapsed);
```

使用 patch/partial result 合并，避免某个 provider 缺少字段时构造完整伪对象。

### Merge Rules

身份字段优先级：

```text
Native protocol > Storage API > MSFT_Disk > Win32_DiskDrive
```

健康字段：

```text
Native NVMe/ATA > Storage Spaces native management status > Unsupported/Unknown
```

同优先级冲突：

- 保留第一个稳定 provider 结果。
- 添加 conflict diagnostic。
- 不在 UI 静默覆盖。

### Cancellation

managed `DeviceIoControl` 同步调用开始后通常不能靠普通 `CancellationToken` 强制终止。

第一阶段语义：

- token 在打开设备前、provider 间和解析前检查。
- IOCTL 在后台线程执行。
- 页面使用 generation/version guard 忽略迟到结果。
- service 对用户显示 timeout，但必须记录底层调用可能仍在返回途中。

如果硬件矩阵发现真实长期阻塞，在进入 USB/RAID 广覆盖前将 IOCTL provider 移入 worker 进程。不要声称 managed cancellation 已终止 kernel I/O。

## Milestone 5: Windows Storage Native Layer

### StorageDeviceHandle

使用：

```csharp
SafeFileHandle CreateFileW(
    string lpFileName,
    uint dwDesiredAccess,
    uint dwShareMode,
    ...);
```

路径：

```text
\\.\PhysicalDrive0
```

共享模式：

```text
FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
```

打开策略：

1. identity/property query 先使用 `dwDesiredAccess = 0`。
2. 如果某个 protocol query 文档要求 read access，再使用 `GENERIC_READ`。
3. ATA SMART 若驱动要求 write access，只在管理员模式下针对该 provider 使用最小必要 access，并在 UI 标记权限要求。
4. 不因为一个 provider 的权限要求提升所有 query 权限。

### DeviceIoControl Wrapper

wrapper 输入：

```csharp
StorageIoControlResult Query(
    SafeFileHandle handle,
    uint controlCode,
    ReadOnlySpan<byte> input,
    int outputLength);
```

要求：

- output length 设置硬上限。
- 使用 checked offset arithmetic。
- 记录 returned bytes。
- Win32 false 时捕获 `Marshal.GetLastWin32Error()`。
- 不把未初始化 output buffer 当有效数据。
- 所有 handles 使用 `using`。

### Native Structures

需要验证并实现：

```text
STORAGE_PROPERTY_QUERY
STORAGE_DESCRIPTOR_HEADER
STORAGE_DEVICE_DESCRIPTOR
STORAGE_ADAPTER_DESCRIPTOR
STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR
STORAGE_PROTOCOL_SPECIFIC_DATA
STORAGE_PROTOCOL_DATA_DESCRIPTOR
```

实现规则：

- 使用 `[StructLayout(LayoutKind.Sequential)]` 只描述固定 header。
- variable arrays 和 string offsets 使用 span parser。
- 不用 `PtrToStructure` 解析 driver 声明的任意长度尾部。
- 每个 string offset 必须 `< returnedBytes`，读取到 NUL 或 buffer end。
- descriptor `Size` 和 `Version` 必须满足最小 header。

### Storage Property Queries

第一批：

```text
StorageDeviceProperty
StorageAdapterProperty
StorageAccessAlignmentProperty
StorageDeviceTrimProperty
StorageDeviceWriteCacheProperty, when reliable
StorageDeviceProtocolSpecificProperty
StorageAdapterProtocolSpecificProperty, when needed
```

输出 typed records，Hardware.Storage 层不读取 raw buffer。

### Bus Detection

优先使用 `STORAGE_DEVICE_DESCRIPTOR.BusType`，其次 `MSFT_Disk.BusType`，最后 WMI `InterfaceType`。

unknown bus 必须保留，不从 `Model.Contains("NVMe")` 直接确认为 NVMe。型号字符串只能生成低置信度 hint。

### Native Layer Tests

真实 P/Invoke 不放入默认 unit tests。把 buffer validation/parser 抽成 pure methods，通过合成 descriptor bytes 测试：

- valid offsets。
- offset 0。
- offset == buffer length。
- unterminated string。
- descriptor size 超过 returned bytes。
- unknown bus enum。

## Milestone 6: NVMe Protocol Query

### Query Shape

使用 `IOCTL_STORAGE_QUERY_PROPERTY` 和 protocol-specific property payload。

Identify Controller：

```text
ProtocolType = ProtocolTypeNvme
DataType = NVMeDataTypeIdentify
ProtocolDataRequestValue = Identify CNS Controller
ProtocolDataRequestSubValue = 0
ProtocolDataLength = 4096
```

SMART / Health Information：

```text
ProtocolType = ProtocolTypeNvme
DataType = NVMeDataTypeLogPage
ProtocolDataRequestValue = 0x02
ProtocolDataRequestSubValue = namespace/controller scope as required
ProtocolDataLength = 512
```

在发送前计算：

```text
ProtocolDataOffset >= sizeof(STORAGE_PROTOCOL_SPECIFIC_DATA)
input/output buffer contains descriptor + protocol data
```

返回后验证：

- descriptor Version/Size。
- ProtocolType/DataType 与请求一致。
- ProtocolDataOffset 不指向 header 之前。
- offset + length 不 overflow 且不超过 returned bytes。
- health payload 至少 512 bytes。

### NvmeSmartHealthLog Model

```csharp
internal sealed record NvmeSmartHealthLog(
    byte CriticalWarning,
    ushort CompositeTemperatureKelvin,
    byte AvailableSparePercent,
    byte AvailableSpareThresholdPercent,
    byte PercentageUsed,
    UInt128 DataUnitsRead,
    UInt128 DataUnitsWritten,
    UInt128 HostReadCommands,
    UInt128 HostWriteCommands,
    UInt128 ControllerBusyMinutes,
    UInt128 PowerCycles,
    UInt128 PowerOnHours,
    UInt128 UnsafeShutdowns,
    UInt128 MediaAndDataIntegrityErrors,
    UInt128 ErrorInformationLogEntries,
    uint WarningCompositeTemperatureMinutes,
    uint CriticalCompositeTemperatureMinutes,
    IReadOnlyList<ushort> TemperatureSensorsKelvin,
    byte[] RawBytes);
```

保留 raw bytes 仅在 report/export 明确需要时编码为 hex；UI 不绑定 512-byte array。

### Parser Offsets

parser 使用 `BinaryPrimitives` 和固定 offset：

```text
0       Critical Warning, 1 byte
1       Composite Temperature, UInt16 LE Kelvin
3       Available Spare, 1 byte
4       Available Spare Threshold, 1 byte
5       Percentage Used, 1 byte
32      Data Units Read, UInt128 LE
48      Data Units Written, UInt128 LE
64      Host Read Commands, UInt128 LE
80      Host Write Commands, UInt128 LE
96      Controller Busy Time, UInt128 LE
112     Power Cycles, UInt128 LE
128     Power On Hours, UInt128 LE
144     Unsafe Shutdowns, UInt128 LE
160     Media/Data Integrity Errors, UInt128 LE
176     Error Information Log Entries, UInt128 LE
192     Warning Composite Temperature Time, UInt32 LE
196     Critical Composite Temperature Time, UInt32 LE
200     Temperature Sensor 1-8, 8 x UInt16 LE
```

任何 offset 变更必须引用对应 NVMe spec/version 并增加 fixture test。

### UInt128 Parsing And Formatting

.NET 8 使用 `UInt128`：

```csharp
low = BinaryPrimitives.ReadUInt64LittleEndian(span[..8]);
high = BinaryPrimitives.ReadUInt64LittleEndian(span[8..16]);
value = ((UInt128)high << 64) | low;
```

NVMe Data Unit：

```text
1 data unit = 1000 x 512 bytes = 512,000 bytes
```

转换需处理 `UInt128 * 512000` overflow。推荐：

- exact counter 保持 `UInt128` 十进制字符串。
- 人类可读 bytes 使用 `BigInteger` 或安全 decimal/scaled conversion。
- 不先转 `ulong`。

### Temperature

- 0 Kelvin 或明显无效 sentinel -> unavailable。
- Celsius = Kelvin - 273.15。
- UI 默认整数或一位小数。
- sensor 0 值不显示。

### Critical Warning Bits

至少解码：

```text
bit 0 Available spare below threshold
bit 1 Temperature threshold exceeded
bit 2 NVM subsystem reliability degraded
bit 3 Media placed in read-only mode
bit 4 Volatile memory backup failed
bit 5 Persistent memory region read-only, when supported
```

未知高位保留为 raw warning，不忽略。

### Health Evaluation

纯函数：

```csharp
StorageHealthEvaluation EvaluateNvme(NvmeSmartHealthLog log);
```

规则顺序：

1. read-only/reliability critical warning -> Critical。
2. 其他 critical warning -> 至少 Caution，按位定义升级。
3. spare < threshold -> Caution。
4. percentage used >= 100 -> Caution。
5. 无 warning -> Good。

历史 media errors 非零需要显式 flag，但不能仅凭一个历史计数自动宣称当前设备 Critical。第一版标记 Caution，并在 reason 中说明是累计错误。

剩余寿命：

```text
max(0, 100 - min(PercentageUsed, 100))
```

source 必须是 NVMe，note 写明来自 Percentage Used。

### Tests

- all-zero normal payload。
- 300 K -> 26.85 C。
- invalid 0 K。
- each critical warning bit。
- unknown warning bit。
- spare below/equal/above threshold。
- percentage used 0/3/99/100/255。
- UInt128 high word non-zero。
- max UInt128 formatted without overflow。
- truncated 511-byte payload。
- offset+length overflow。

## Milestone 7: StorageDetailService

### App Registration

修改：

```text
src/HwScope.App/App.xaml.cs
```

新增：

```csharp
public static StorageDetailService StorageDetails { get; private set; } = null!;
```

在 `HardwarePreloadService` 后创建。

### Responsibilities

- 从 `HardwarePreloadService.Current.DiskDrives` 建立 device descriptor 列表。
- 按 stable ID 缓存最近成功 report。
- 合并同一设备并发请求。
- 同一设备合并为一个 active task；不同设备彼此隔离，避免单个阻塞驱动占住全局 gate 后拖住全部磁盘。
- UI 等待采用 5 秒 soft timeout；底层同步 IOCTL 可能继续运行并在迟到后更新设备缓存。
- 记录当前每设备状态和 error。
- inventory changed 时移除已不存在设备的 cache。
- 将事件发布回 Dispatcher。

### API

```csharp
public sealed class StorageDetailService
{
    public IReadOnlyList<StorageDeviceDescriptor> Devices { get; }

    public event EventHandler<StorageDeviceListChangedEventArgs>? DevicesChanged;
    public event EventHandler<StorageReportChangedEventArgs>? ReportChanged;

    public void SynchronizeInventory(HardwareInventorySnapshot snapshot);

    public StorageDetailReport? TryGetCached(string stableId);

    public Task<StorageDetailReport> EnsureLoadedAsync(
        string stableId,
        CancellationToken cancellationToken = default);

    public Task<StorageDetailReport> RefreshAsync(
        string stableId,
        CancellationToken cancellationToken = default);
}
```

### Concurrency

不要只用一个全局 `_loadTask`，因为设备选择会变化。

建议内部：

```text
Dictionary<stableId, DeviceLoadState>
DeviceLoadState
  SemaphoreSlim Gate
  Task<StorageDetailReport>? ActiveTask
  StorageDetailReport? Current
  long Generation
```

页面切换磁盘不会取消其他调用方，但页面自己的 cancellation/version guard 会忽略旧结果。

### Cache Semantics

- `EnsureLoadedAsync`：有缓存立即返回。
- `RefreshAsync`：启动新读取；已有正在读取任务时合并。
- report 保留 `GeneratedAt`。
- UI 明确显示缓存时间。
- 失败不删除上次成功 report；service 同时发布 latest error。

## Milestone 8: Storage Detail Page Shell

### MainWindow Integration

修改：

```text
src/HwScope.App/MainWindow.xaml
src/HwScope.App/MainWindow.xaml.cs
```

导航：

```xml
<ui:NavigationViewItem Content="存储设备"
                       Tag="storage-detail"
                       Icon="{ui:SymbolIcon HardDrive20}"
                       Click="NavigationItem_Click" />
```

MainWindow：

```csharp
private readonly StorageDetailPage _storageDetailPage = new();
```

接入 `StatusChanged`，route 中增加 `storage-detail`。

### Page State

```csharp
private string? _selectedDeviceId;
private StorageDetailReport? _currentReport;
private int _refreshVersion;
private CancellationTokenSource? _selectionCancellation;
private bool _loadedOnce;
private bool _isSubscribed;
```

### Initial Selection

优先顺序：

1. volume mapping 中承载系统目录的磁盘。
2. `MSFT_Disk.IsSystem`。
3. 最低 physical drive index。
4. inventory 顺序第一项。

没有设备时显示稳定 empty state，不弹 MessageBox。

### Async Flow

```csharp
private async Task SelectDeviceAsync(string stableId, bool forceRefresh)
{
    var version = Interlocked.Increment(ref _refreshVersion);
    CancelPreviousSelectionWait();
    RenderCachedOrBaseline(stableId);
    SetSelectedDeviceBusy(true);

    try
    {
        var report = forceRefresh
            ? await App.StorageDetails.RefreshAsync(stableId, token)
            : await App.StorageDetails.EnsureLoadedAsync(stableId, token);

        if (version != _refreshVersion || stableId != _selectedDeviceId)
        {
            return;
        }

        Render(report);
    }
    catch (OperationCanceledException) when (token.IsCancellationRequested)
    {
    }
    catch (StorageCollectionException ex)
    {
        RenderErrorWithoutDiscardingCachedReport(ex);
    }
    finally
    {
        if (version == _refreshVersion)
        {
            SetSelectedDeviceBusy(false);
        }
    }
}
```

### XAML Structure

```text
Grid
  Row 0 Header
  Row 1 ScrollViewer
    StackPanel
      Device selector ItemsControl / WrapPanel
      Selected disk header band
      Adaptive summary sections
      Interface and features section
      Protocol attribute section
      Volume/partition section
      Notes/diagnostics section
```

Named controls：

```text
StorageSubtitleText
RefreshButton
CopyButton
SaveButton
DeviceTilesList
SelectedDeviceTitleText
SelectedDeviceMetaText
HealthStatusText
HealthReasonText
RemainingLifeText
TemperatureText
SummarySectionList
FeatureChipsList
AttributeFilterControl
NvmeAttributesGrid
AtaAttributesGrid
UnsupportedHealthPanel
VolumeList
NotesList
```

### View Records

```csharp
public sealed record StorageDeviceTileView(...);
public sealed record StorageSectionView(...);
public sealed record StorageFieldRowView(...);
public sealed record NvmeAttributeRowView(...);
public sealed record AtaAttributeRowView(...);
public sealed record StorageVolumeRowView(...);
```

### Device Tiles

固定尺寸：

```text
Width 240
MinHeight 88
Margin 0,0,10,10
```

tile 显示：

- Disk number / bus。
- model / capacity。
- health。
- temperature。
- drive letters/access paths。

加载时不能改变 tile 大小。

### Health Panel

不用 CrystalDiskInfo 渐变按钮。使用：

- status icon。
- `良好/注意/严重/未知/不支持` 文本。
- optional remaining life。
- visible reason。
- temperature 单独字段。

### Attribute Filter

使用 segmented control 或三个 toggle-like buttons：

```text
全部 | 异常 | 关键
```

默认全部。过滤只影响 UI，不改变 report/export。

### DataGrid

使用 WPF `DataGrid` 或轻量 list grid，但必须：

- `IsReadOnly=true`。
- `EnableRowVirtualization=true`。
- `EnableColumnVirtualization=true`。
- 固定 row height。
- raw column 等宽字体。
- 用户排序不能改变 report 原始顺序。
- NVMe 和 ATA 使用两个独立 grid，按 protocol 显示其一。

### No Nested Scroll Failure

根内容可以滚动，但 DataGrid 若无限高度会失去 virtualization。

初始实现选择：

- attribute grid 设置 `MinHeight=320`、`MaxHeight=520`。
- grid 内部垂直滚动。
- 根 ScrollViewer 负责页面其他 section。

需要手动验证滚轮行为，避免用户困在嵌套滚动区。

## Milestone 9: Theme Tokens And Styles

修改：

```text
src/HwScope.App/Themes/Json/light.json
src/HwScope.App/Themes/Json/dark.json
src/HwScope.App/Theming/ThemeDefinitionStore.cs
src/HwScope.App/Themes/HwScope.Controls.xaml
```

新增 token：

```text
HwScopeStatusGoodColor
HwScopeStatusCautionColor
HwScopeStatusCriticalColor
HwScopeStatusUnknownColor
HwScopeStatusInfoColor
```

`ThemeResourceBuilder` 会生成 Brush。把新 token 加入 required-token validation，确保 theme 缺失时 fallback，而不是运行期找不到 resource。

共享样式：

```text
StorageDeviceTileButtonStyle
StorageStatusBadgeStyle
StorageAttributeGridStyle
StorageRawValueTextStyle
```

要求：

- light/dark 对比度可读。
- Caution 不是仅用黄色浅底和白字。
- status icon 和文字同时存在。
- 不引入 gradient brush。

## Milestone 10: Formatter And Export

### Text Formatter

新增：

```text
StorageDetailReportFormatter.cs
```

稳定顺序：

```text
Identity
Interface
Health
Lifetime
Protocol Attributes
Partitions
Volumes
Notes
Diagnostics Summary
Generated At
```

每个字段附来源：

```text
Temperature: 49 C [NVMe]
Remaining Life: 97% [NVMe/Computed]
```

raw 值：

- NVMe table 输出 standard raw hex/decimal counter。
- ATA 输出 raw six bytes 和 decoded value。

默认不输出：

- 完整 exception stack。
- 默认 crash log path。
- 用户未请求时的完整 device instance ID。

### Copy/Save

页面 Copy/Save 与 CPU/内存一致：

- copy 当前设备完整 report。
- save `.txt`。
- 当前文件名使用 `HwScope-Storage-Disk{N}-{timestamp}.txt`；磁盘编号不可用时使用 `unknown`。

### Future UI JSON

页面工具栏 JSON export 不属于首个交付。CLI 已经通过 `storage --disk N --json` 序列化 Core domain report；未来 UI 导出也应复用 domain report，不序列化 WPF view records。

## Milestone 11: ATA SMART

NVMe 首版完成后接 ATA，不与 NVMe parser 混写。

### Windows IOCTL Path

实现：

```text
SMART_GET_VERSION
SMART_RCV_DRIVE_DATA
IOCTL_ATA_PASS_THROUGH + ATA_PASS_THROUGH_EX, for RETURN_SMART_STATUS
```

结构：

```text
GETVERSIONINPARAMS
IDEREGS
SENDCMDINPARAMS
DRIVERSTATUS
SENDCMDOUTPARAMS
ATA_PASS_THROUGH_EX
CurrentTaskFile
```

legacy SMART buffer 使用 SDK 定义的固定 layout；ATA pass-through 根据进程 pointer size 使用 40/48-byte `ATA_PASS_THROUGH_EX` layout，并校验返回 task-file signature。provider 不暴露任意 ATA command 输入。

允许的 SMART feature commands 仅限常量：

```text
READ_ATTRIBUTES
READ_THRESHOLDS
RETURN_STATUS
```

provider 不暴露 `byte feature` 公共输入。

### ATA SMART Data Parser

512-byte attribute sector：

```text
offset 0   revision, UInt16 LE
offset 2   30 entries x 12 bytes

entry
  id       1 byte
  flags    UInt16 LE
  current  1 byte
  worst    1 byte
  raw      6 bytes
  reserved 1 byte
```

threshold sector 同样按 ID 合并 threshold。ID 0 entry 跳过。attribute/threshold sector 必须具有非零 revision、有效 512-byte checksum 和至少一个有效 attribute，否则返回 `MalformedResponse`，不得进入 `Good` 判定。

### Attribute Catalog

第一版只对以下常见 ID 提供保守名称/解码：

```text
0x05 Reallocated Sector Count
0x09 Power-On Hours
0x0C Power Cycle Count
0xC2 Temperature
0xC4 Reallocation Event Count
0xC5 Current Pending Sector Count
0xC6 Offline Uncorrectable
0xC7 UDMA CRC Error Count
```

注意：

- 即使 ID 常见，raw layout 仍可能有厂商差异。
- SSD wear indicator IDs 不进入通用规则。
- catalog 输出 note/interpretation confidence。

### ATA Health Evaluation

```text
SMART overall failed -> Critical
pre-fail current <= valid threshold -> Critical
pending/uncorrectable/reallocated > 0 -> Caution
CRC error > 0 -> Caution note, but may indicate cable/link rather than media
overall passed and no known issue -> Good
overall unavailable and no known issue -> Unknown
attribute query unavailable/pass-through blocked -> Unknown or Unsupported, with provider diagnostic
```

历史 non-zero counter 不自动升级到 Critical。

### Tests

当前 fixture tests 已覆盖：

- attribute/threshold merge 和 raw little-endian integer。
- pre-fail threshold crossing。
- RETURN STATUS passed/failed signature。
- overall unavailable -> Unknown。
- empty/revision、sector checksum 和 malformed short buffer rejection。

仍待补充：

- 完整 30-entry sector 和 duplicate-ID diagnostic。
- 更多 ATA temperature raw layout。
- vendor-specific attribute catalog fixture。
- 直连 SATA SSD/HDD 和 USB/SAT bridge 硬件矩阵。

## Milestone 12: CLI Diagnostic Entry

CLI 不是页面交付的硬性前置，但应在 NVMe provider 稳定后加入，便于无 GUI 验证。

命令：

```powershell
HwScope.Cli storage list
HwScope.Cli storage --disk 0
HwScope.Cli storage --disk 0 --json
```

行为：

- `list` 列 baseline identity，并使用轻量 Windows Storage descriptor 查询真实 bus；descriptor 失败时回退 WMI `InterfaceType`，不触发 NVMe Health 或 ATA SMART。
- `--disk` 读取指定物理设备详情。
- JSON 使用 Core domain model。
- 当前退出码：未找到磁盘为 `3`，健康状态 `Critical` 为 `4`，未处理异常为 `1`，其余成功报告为 `0`；provider 的权限/unsupported 细节保留在 report diagnostics。
- 不接受 raw device path 或 opcode 参数。

已知 CLI 参数校验缺口：当前传入无法解析的 `--disk` 值会退化为 list 行为并返回 `0`。这属于待修复的参数解析问题，不是受支持语义。

## Milestone 13: Documentation Updates

实现过程中同步更新：

```text
README.md
docs/project-architecture.md
docs/hardware-preload-design.md
docs/storage-detail-page-design.md
docs/storage-detail-implementation-plan.md
```

README 只有在功能真实可用后才加入“当前已具备”。在此之前只保留 docs 索引。

需要记录支持矩阵：

```text
Bus / protocol
Windows version
Admin required
Identity
Temperature
Health
Attributes
Known limitation
```

## Detailed Commit Sequence

建议每个提交都可构建：

1. `docs: design storage detail page`
2. `test: add core storage parser test project`
3. `feat: enrich disk inventory identity`
4. `feat: map storage disks partitions and volumes`
5. `feat: add storage detail domain models`
6. `feat: add storage provider aggregation`
7. `feat: query Windows storage descriptors`
8. `feat: parse NVMe health information`
9. `feat: evaluate NVMe storage health`
10. `feat: add storage detail service`
11. `feat: add storage detail page shell`
12. `feat: render NVMe health and volumes`
13. `feat: add storage text report export`
14. `test: cover storage errors and malformed payloads`
15. `feat: add ATA SMART provider`
16. `docs: document storage support matrix`

如果某一步需要跨多个提交，应保持 model/parser/provider/UI 的依赖方向，不提交一个会显示伪数据的中间 UI。

## Build And Validation Commands

### Managed Build

```powershell
dotnet restore
dotnet build
dotnet test .\src\HwScope.Core.Tests\HwScope.Core.Tests.csproj
```

### CLI Smoke

```powershell
dotnet run --project .\src\HwScope.Cli -- storage list
dotnet run --project .\src\HwScope.Cli -- storage --disk 0
```

### GUI Smoke

```powershell
dotnet run --project .\src\HwScope.App\HwScope.App.csproj
```

检查：

- 启动 preload 不因 SMART 查询变慢。
- `硬件 -> 存储设备` 打开独立页面。
- 多磁盘 tile 与 Windows Disk Management 对应。
- 默认选中系统盘。
- NVMe health 与可信工具/厂商工具交叉核对。
- 刷新不重跑 CPU/内存 inventory。
- 切盘时旧结果不会覆盖新盘。
- Copy/Save 内容与页面一致。
- 普通权限下能降级显示 identity。
- 管理员权限下可尝试更完整 provider。

## Hardware Validation Matrix

至少收集以下环境：

| Device | Expected First Support | Key Checks |
| --- | --- | --- |
| Direct NVMe system SSD | Full NVMe | Identify, temp, percentage used, data units, errors |
| Second direct NVMe SSD | Full NVMe | multi-device selection and stable ID |
| Direct SATA SSD | ATA stage | attributes, thresholds, no fake life percent |
| Direct SATA HDD | ATA stage | RPM, power hours, temperature, no auto polling |
| USB-SATA bridge | Best effort | unsupported vs SAT pass-through |
| USB-NVMe bridge | Best effort | bus/bridge identity, protocol availability |
| Storage Spaces | Management status | virtual/pool labeling, no member guess |
| RAID logical volume | Limited | controller blocked state |
| VHD/VHDX | Identity only | Virtual/Unsupported health |

交叉验证不能只看 CrystalDiskInfo UI 数值，还要核对：

- PowerShell `Get-Disk`。
- `Get-PhysicalDisk`，适用时。
- `Get-Partition` / `Get-Volume`。
- 厂商工具或另一款可信 SMART 工具。

## Failure Scenarios

必须手测：

- 用户取消 UAC，普通权限启动。
- 设备列表为空。
- NVMe protocol property unsupported。
- access denied。
- device removed during query。
- USB disk unplugged while selected。
- malformed/truncated synthetic provider response。
- two disks share model and empty serial。
- system disk contains recovery volume without letter。
- long model/serial/device path。
- report refresh fails after a previous success。

预期：

- 旧成功数据保留并标记时间。
- 新错误可见。
- 不显示错盘。
- 不因单盘失败弹出阻塞全窗口的 exception MessageBox。

## Security Review Checklist

- 所有 IOCTL 是固定 allowlist。
- 没有 raw opcode/public passthrough API。
- output buffer 有上限。
- offset/length 使用 checked arithmetic。
- native handles 始终释放。
- 默认日志不包含完整序列号。
- UI 不展示完整 exception stack。
- tests 不打开真实物理盘。
- 不向系统盘写入。
- 不自动执行 self-test 或唤醒策略未知的设备轮询。
- admin 只提高能力，不改变 health 判定语义。

## Performance Budgets

目标预算：

```text
Render baseline tiles from preload: < 100 ms UI work
MSFT disk/volume mapping: target < 500 ms
Direct NVMe health query: target < 500 ms
Selected-device full first report: target < 1 s
Soft timeout/status transition: 5 s
Hard isolation requirement review: any reproducible > 5 s driver block
```

这些是工程目标，不是同步阻塞时间。所有 provider 均离开 UI 线程。

## Definition Of Done: First Complete NVMe Release

第一版完整 NVMe 存储详情页完成条件：

- 新测试项目存在并在 `dotnet test` 通过。
- `DiskDriveSnapshot` 包含稳定枚举所需身份字段。
- 磁盘、分区、卷映射在测试和当前机器正确。
- `StorageDetailReport`、formatter、provider aggregation 和 diagnostics 完成。
- Windows storage descriptor 查询完成并有 buffer boundary tests。
- NVMe 512-byte Health log 使用真实协议 query 读取。
- NVMe parser 覆盖 warning、temperature、percentage used 和 UInt128 counters。
- 健康判定给出可见 reason，不只显示颜色。
- `StorageDetailService` 区分缓存、刷新、迟到结果和失败。
- `StorageDetailPage` 可选盘、刷新、复制、保存。
- 存储导航不再回到摘要。
- Unsupported、Unknown、AccessDenied、DeviceRemoved 可区分。
- 页面在 light/dark、最小窗口、多盘和长字段下无重叠。
- README 和 architecture 文档只声明已验证能力。

## Definition Of Done: ATA Extension

ATA 阶段的硬件完成条件如下；由于尚未完成直连 SATA SSD/HDD 验证，当前还不能声明全部满足：

- 直连 SATA SSD/HDD 可以读取 overall status、attributes 和 thresholds。
- ATA parser/threshold merge 有 fixture tests。
- ATA 表展示 current/worst/threshold/raw，不套用 NVMe 列。
- health evaluator 对 threshold crossing 和关键 raw 属性给出保守状态。
- 没有通用 ATA 寿命百分比。
- USB bridge 不支持时显示 Unsupported。
- HDD 不默认自动轮询或被无意唤醒。

## Deferred Work

以下不进入首个完整开发周期：

- 温度历史数据库。
- 系统托盘通知。
- SMART self-test UI。
- 固件升级。
- secure erase/sanitize。
- arbitrary protocol command console。
- 完整 CrystalDiskInfo 厂商属性数据库。
- 所有 RAID vendor plugin。
- PDF/HTML 报告。

这些能力必须在基础只读查询、身份匹配和错误模型稳定后单独设计。
