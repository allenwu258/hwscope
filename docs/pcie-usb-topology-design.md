# PCIe And USB Topology Requirements And UI Design

本文档定义 HwScope 的 PCI Express 和 USB 真实树状枚举能力，包括产品需求、拓扑语义、Windows 数据源、Core 模型、采集边界、页面信息架构、交互、错误状态、验证矩阵和分阶段实现计划。

本文档描述的是待开发能力，不代表当前仓库已经实现 PCIe/USB 树枚举。当前仓库只有 CPU topology 的通用绘制骨架；该骨架可在后续作为可视化视图复用，但不能替代本功能需要的强类型设备模型和专业树状诊断页面。

## 1. Background

HwScope 当前已经具备硬件摘要、CPU topology、内存详情和按物理磁盘读取的存储详情，但用户还不能回答以下常见问题：

- NVMe、GPU、网卡和 USB 控制器分别挂在哪个 PCIe Root Port / Bridge 下。
- 某 PCIe 设备当前协商的是 Gen 几、x 几，是否低于设备或上游端口能力。
- 某个 USB 设备连接到哪个 Host Controller、Root Hub、Hub 和物理端口。
- USB 设备、端口和控制器各自支持什么速度，当前连接速度瓶颈在哪里。
- USB Composite Device 下有哪些 interface/function、驱动和 endpoint。
- 空 USB 端口、异常端口、Problem Code、驱动、位置路径和硬件 ID 是什么。
- 从存储、网络或显示设备详情如何反查其 PCIe/USB 上游路径。

用户提供的参考截图体现了两个成熟工具的不同优势：

- HWiNFO 把 PCIe bridge、endpoint、USB controller、设备身份、链路、资源和驱动放进完整系统语境，适合定位“设备挂在哪里”和“链路是否正常”。
- USB Device Tree Viewer 以 Host Controller -> Root Hub -> Port -> Hub/Device 的物理端口链为中心，保留空端口、descriptor、interface、endpoint、驱动和 PnP 细节，适合 USB 枚举与连接故障诊断。

HwScope 不应复制这些工具的旧式视觉外观，但应保留它们的信息密度、可追溯性和树选择效率。

## 2. Product Decisions

以下决策在首个实现中锁定：

1. PCIe 和 USB 使用两个独立但可交叉导航的拓扑快照，不伪装成同一种树。
2. UI 分成三个层次：主页面紧凑拓扑概览、独立诊断窗口、独立 Topology Inspect 窗口。
3. 三个界面消费同一份 Core snapshot 和详情缓存，不得各自重新枚举硬件。
4. Core 使用强类型、不可变模型；UI 不解析 Location Path、descriptor bytes 或 PCI/USB capability。
5. Windows Configuration Manager / SetupAPI 是 PnP 身份和父子关系的权威来源。
6. USB 物理端口树以 USB hub IOCTL 为权威来源，不能只用 WMI/SetupAPI 平铺 USB 设备。
7. PCIe 第一阶段只使用 Windows 支持的用户态只读属性和资源接口，不读取任意 PCI configuration space。
8. 所有字段带来源、可用性和必要的推导说明；未知值不能默认显示为 0、x1、Gen 1 或“正常”。
9. PCIe 属于相对静态拓扑；USB 属于热插拔动态拓扑。二者刷新策略分开。
10. 首次打开主页面按需采集，不把完整 USB port/descriptor 扫描塞进启动 preload。
11. 首版不做自动周期轮询，不发送 vendor control transfer，不改变设备、电源、驱动或端口状态。

## 3. Goals

### 3.1 Topology Goals

- 构建可追溯的 PCI/PCIe Root -> Host/Root Bridge -> PCIe Bridge/Port -> Endpoint/Function 树。
- 构建 Host Controller -> Root Hub -> Port -> External Hub -> Port -> Physical Device 树。
- 可选展示 USB physical device 下的 PnP interface/function 层。
- 保留 BDF、Location Path、Port Chain、PnP Instance ID 和 Container ID 等稳定诊断坐标。
- 在 PCIe 与 USB 之间建立 USB Host Controller 的交叉链接。
- 为存储、网络、GPU 等后续详情页提供稳定的 topology node reference。

### 3.2 Diagnostic Goals

- 展示 PCIe 当前/最大链路 generation、GT/s 和 width，明确数据不可用与推导值。
- 展示 PCI identity、class code、subsystem、revision、resources、driver 和 PnP status。
- 展示 USB 协议版本、端口能力、设备最大速度、当前连接速度、power demand 和 endpoint。
- 显示断开、禁用、Problem Code、descriptor malformed、controller blocked 和 timeout 等状态。
- 对明显的协商降级给出信息性诊断，但不把“低于最大能力”直接判定为硬件故障。
- 支持搜索、过滤、路径复制、当前节点报告和完整树导出。

### 3.3 UX Goals

- 在大量 bridge、空端口和 composite interface 中仍能快速定位目标。
- 选择节点后不丢失拓扑上下文；主页面下方详情和诊断窗口右侧详情都按固定分组呈现。
- 刷新后尽量保持选中节点、展开状态、滚动位置和搜索状态。
- 热插拔过程中不闪烁整页、不把旧设备详情显示在新设备标题下。
- 在主窗口最小宽度、浅色/深色主题和高 DPI 下不发生文字重叠。

## 4. Non-Goals

首个工作流明确不包含：

- 任意 PCI config space 读取、写入或用户提供 offset。
- PCIe link retrain、ASPM 切换、Resizable BAR 修改或设备 reset。
- USB port reset、cycle、disable、eject、descriptor fuzzing 或 vendor command。
- 驱动安装/卸载/更新、设备启用/禁用。
- IOMMU group、完整 ACPI namespace 或芯片组内部 fabric 拓扑。
- 完整 USB4 router、Thunderbolt fabric、DisplayPort tunnel 或 PCIe tunnel 管理视图。
- 将物理主板插槽、外壳 Type-C 插孔与逻辑端口做未经验证的映射。
- 自动下载在线 PCI/USB ID 数据库。
- 用启发式名称掩盖 Windows/设备未提供的事实。

## 5. Topology Semantics

### 5.1 Three Different Trees

Windows 上至少存在三种容易混淆的层级：

```text
PnP DevNode Tree
  Windows 对设备、驱动和枚举器的父子关系

PCI/PCIe Topology
  Root/Host Bridge、PCIe Bridge/Port、Endpoint/Function 和 BDF 关系

USB Physical Port Tree
  Host Controller、Root Hub、Port、External Hub、Physical Device
```

它们可以互相引用，但不能互相替代：

- PnP parent 不总是用户期望的物理 connector parent。
- USB device 的 PnP 子节点常常是 composite interface/function，而不是下游物理端口。
- PCIe endpoint 的 Windows 设备节点能表达驱动和位置，但部分链路 capability 不一定公开。
- 一个 Type-C 物理插孔可能对应 USB 2 companion port、SuperSpeed port、USB4 router 和 Alternate Mode；不能只凭相邻端口号强行合并。

### 5.2 Truth And Confidence

每个字段必须属于以下一种语义：

| Kind | Meaning | UI |
| --- | --- | --- |
| Reported | Windows API、bus driver 或标准 descriptor 直接报告 | 正常来源 badge |
| Correlated | 通过 driver key、instance ID、Container ID 或 Location Path 关联 | `关联` badge，tooltip 给出键 |
| Derived | 从标准字段计算，如 Gen/GT/s 文本或 USB port chain | `推导*` badge |
| Heuristic | 缺少权威映射时的弱推断 | 默认不进入核心树；可在诊断说明中展示 |
| Unavailable | 数据源未提供、权限不足或版本不支持 | 明确显示原因 |

Heuristic 不得改变父子结构、健康状态或设备身份。

## 6. Primary User Scenarios

### 6.1 Locate A PCIe Device

用户搜索 NVMe 型号或 `VEN_144D&DEV_A80C`：

1. 搜索命中 endpoint。
2. 树自动展开 Root -> Bridge -> Endpoint 路径。
3. 右侧显示 `0000:03:00.0`、Location Path、当前/最大链路、资源、驱动。
4. 面包屑可复制完整上游路径。

### 6.2 Diagnose A Link Downgrade

用户选择 GPU/NVMe/网卡：

- 同时看到 endpoint 当前/最大链路。
- 如果 endpoint 最大 x4、当前 x2，显示“当前协商宽度低于设备报告的最大宽度”。
- 如果设备空闲导致 speed 降低且 Windows 只报告当前状态，不直接标为异常。
- 上游 bridge 可一键跳转，用于检查共同瓶颈。

### 6.3 Locate A USB Physical Port

用户选择 USB 音频设备：

- 树路径为 Controller -> Root Hub -> Port 1 -> External Hub -> Port 2 -> Device。
- 详情显示 port chain、VID/PID、bcdUSB、设备最大速度和当前连接速度。
- 可复制 `4-1-2` 一类端口链以及 Windows Location Path。
- composite functions 在设备下按需展开，而不是与物理设备平级混杂。

### 6.4 Diagnose USB Speed Or Power

- 同时展示 controller/port supported protocols、device maximum speed、connection speed。
- 显示 descriptor 声明的 maximum power，明确它不是实时功耗。
- 如果 10 Gbps 设备当前以 High-Speed 连接，显示可能原因：USB 2 companion path、线缆、Hub 或端口能力；不直接宣告设备损坏。

### 6.5 Inspect Empty And Problem Ports

- 开启“显示空端口”后保留所有 hub port。
- 端口显示 No Device、Disabled、OverCurrent、FailedEnumeration 等可见状态。
- “仅异常”过滤器保留异常节点及其所有祖先，避免命中节点失去路径。

## 7. Functional Requirements

### 7.1 Common Requirements

- 主页面提供 `PCI Express` / `USB` 两个标签。
- 每个模式有独立的选中项、展开状态和过滤状态。
- 主页面提供 `诊断` 和 `拓扑全貌` 两个命令，分别打开单实例窗口并传递当前模式/节点。
- 三个 surface 都支持刷新当前 mode；诊断窗口和 Inspect 支持搜索、上一个/下一个命中和展开/高亮当前路径。
- 主页面支持复制当前节点摘要和保存节点报告；诊断窗口/Inspect 支持完整 JSON 导出。
- Compact map node 和诊断 tree row 显示图标、主标签、紧凑坐标和状态，不在节点内塞入完整详情。
- 详情字段使用稳定顺序；刷新时 unavailable 不导致 section 大幅跳动。
- 所有节点提供 stable ID，刷新时按 stable ID 恢复状态。
- 快照记录生成时间、总耗时、分支诊断和数据源版本。

### 7.2 PCIe Tree Requirements

PCI 树至少支持：

- PCI/PCIe root 或 Windows 可识别的根节点。
- Host Bridge / Root Port / Downstream Port / PCI-to-PCI Bridge。
- Endpoint 和 multi-function function。
- 显卡、NVMe、网卡、USB controller、音频和系统设备等 class。
- Bus/Device/Function 地址；segment 无法确认时显示 `????:bb:dd.f` 或只显示 `bb:dd.f`。
- Location Path、Location Information、PnP Instance ID、Container ID。
- Vendor ID、Device ID、Subsystem Vendor/Device ID、Revision。
- Base Class、Sub Class、Programming Interface 和友好 class 名。
- 当前/最大 link speed、generation、GT/s 和 width，数据不可用时明确说明。
- Max Payload Size、Max Read Request、ASPM/L0s/L1、Resizable BAR 等仅在 Windows 属性真实提供时展示。
- IRQ、MSI/MSI-X 可用信息、Memory/Port resource ranges。
- Driver provider、description、version、date、INF、service。
- DevNode status、Problem Code、Capabilities、Enumerator。

### 7.3 USB Tree Requirements

USB 树至少支持：

- Host Controller 和 driver/provider。
- Root Hub、external hub、port count 和 hub characteristics。
- 所有 port，包括空端口，可由用户隐藏。
- 每个 port 的 connection status、port chain、connector/companion 信息。
- Physical USB Device，包括 VID/PID、bcdDevice、bcdUSB、class/subclass/protocol。
- Manufacturer/Product/Serial string descriptor，读取失败单独记录。
- Port maximum capability、device maximum speed 和 current connection speed。
- Active configuration、self/bus powered、descriptor maximum power。
- Interface、alternate setting 和 endpoint 地址/方向/type/max packet/interval。
- Composite device 下的 function/PnP child，可折叠显示。
- Driver key、device path、instance ID、hardware IDs、service、driver package。
- Container ID、Location Path、Capabilities、DevNode status 和 Problem Code。
- USB 2/SuperSpeed companion port 关联，在 Windows connector properties 提供时展示。

### 7.4 Search Requirements

搜索范围包括：

- 显示名称、厂商、产品、序列号。
- VID/PID、VEN/DEV/SUBSYS、class code、BDF。
- PnP Instance ID、Hardware IDs、Container ID。
- Driver/service/INF。
- Location Path、port chain。

搜索行为：

- 150-250 ms debounce。
- 命中节点高亮；保留祖先。
- 支持 `VID:PID`、`VEN:DEV`、`bb:dd.f` 的规范化匹配。
- 默认不把序列号写入搜索历史或日志。

### 7.5 Filter Requirements

PCIe filters：

- 全部 / Endpoint / Bridge / Controller。
- 仅存在设备。
- 仅异常设备。
- 隐藏无驱动的内部 pseudo function，可选且默认关闭。

USB filters：

- 显示空端口。
- 显示 interface/function。
- 仅已连接设备。
- 仅异常端口/设备。
- 按 Low/Full/High/Super/SuperPlus speed 过滤。

过滤不能改变快照，只改变 visible tree projection。

## 8. Field Design

### 8.1 Common Field Wrapper

```csharp
public sealed record TopologyFieldValue<T>(
    T? Value,
    string DisplayText,
    DeviceTopologyDataSource Source,
    bool IsAvailable,
    bool IsDerived = false,
    string? Note = null);
```

建议来源枚举：

```csharp
public enum DeviceTopologyDataSource
{
    Unknown,
    ConfigurationManager,
    SetupApi,
    PciBusProperty,
    UsbHubIoctl,
    UsbDescriptor,
    PnpCorrelation,
    Computed,
    Placeholder
}
```

UI 不能根据 `Value == 0` 判断 unavailable，必须使用 `IsAvailable`。

### 8.2 PCIe Detail Groups

#### Overview

| Field | Preferred Source |
| --- | --- |
| Device name / original name | DEVPKEY Device properties |
| Node kind | PCI class + parent/child relationship |
| BDF | BusNumber/Address + verified Location Path |
| Hardware IDs | SetupAPI/CM |
| Vendor/device/subsystem/revision | Hardware IDs or PCI properties |
| Class/subclass/prog-if | PCI bus property or compatible IDs |
| Location path | DEVPKEY_Device_LocationPaths |
| Instance / Container ID | SetupAPI/CM |

#### Link And Capabilities

| Field | Rule |
| --- | --- |
| Current generation / speed | Only when pci.sys/Windows exposes it |
| Current width | Never infer from device name |
| Maximum generation / width | Device/port property when available |
| GT/s | Map from reported generation/speed; mark derived |
| Effective bandwidth | Optional derived field; encoding-aware and clearly labeled |
| ASPM/L0s/L1/L1 Substates | Only reported capabilities/status |
| Payload / read request | Only reported properties |
| Resizable BAR / SR-IOV / AER/ACS | Do not promise unless data source is verified |

For PCIe 6.0 and later, do not apply a simplistic Gen 3-5 bandwidth formula. Display reported generation, width and GT/s first.

Link 字段描述当前 function/port 相邻的 PCIe link，不等于整条 Root-to-Endpoint 路径。UI 必须使用“当前节点链路”一类准确标题。后续若提供“路径瓶颈”，应遍历所有可用 ancestor link，只有路径数据完整时才计算最小 generation/width/capacity；数据不完整时显示“无法确定完整路径瓶颈”。

#### Resources

- IRQ / message-signaled interrupt information where exposed.
- Memory ranges, prefetchable flag where exposed.
- I/O port ranges.
- Bus number ranges for bridges.
- Resource conflicts or unavailable state.

#### Driver And PnP

- Driver description/provider/version/date/INF/service.
- Class/Class GUID/Enumerator.
- DevNode status flags and Problem Code.
- Capabilities: removable, surprise removal, disableable, eject supported。

### 8.3 USB Detail Groups

#### Connection

- Connection status。
- Controller/root hub/port chain。
- Port number and connector properties。
- Supported port protocols。
- Device maximum speed。
- Current connection speed。
- Open pipes/endpoints count。
- Companion hub/port when reported。

#### Device Descriptor

- `idVendor`, `idProduct`, `bcdDevice`, `bcdUSB`。
- Device class/subclass/protocol。
- `bMaxPacketSize0`、configuration count。
- Manufacturer/product/serial string index and resolved text。

#### Configuration / Interface / Endpoint

- Configuration value、attributes、maximum power。
- Interface number、alternate setting、class/subclass/protocol。
- Endpoint address、IN/OUT、transfer type、max packet、interval。
- BOS capabilities can be added only after a bounded parser and fixtures exist。

#### Power

- Self powered / bus powered from configuration descriptor。
- Descriptor maximum power with USB-version-correct units。
- Do not label descriptor maximum as measured current or USB-PD negotiated power。
- USB-PD contract/charger telemetry is a separate future provider。

#### Driver And PnP

- Driver key、instance/device path、hardware/compatible IDs。
- Driver provider/version/date/INF/service。
- Container ID、Location Path、Capabilities、status/problem code。
- Composite child functions and their class drivers。

## 9. Core Domain Model

PCIe 和 USB 应有独立 typed records，共享 identity/status/field wrapper，不直接复用 App 的 `TopologyNode.Properties` 字典。

```text
HwScope.Core/Hardware/DeviceTopology/
  DeviceTopologyDataSource.cs
  TopologyFieldValue.cs
  PnpDeviceIdentity.cs
  DeviceNodeStatus.cs
  DeviceTopologyDiagnostics.cs

  Pci/
    PciTopologySnapshot.cs
    PciTopologyNode.cs
    PciAddress.cs
    PciIdentity.cs
    PciLinkInfo.cs
    PciCapabilityInfo.cs
    PciResourceInfo.cs
    PciTopologyCollector.cs

  Usb/
    UsbTopologySnapshot.cs
    UsbTopologyNode.cs
    UsbPortInfo.cs
    UsbDeviceDescriptorInfo.cs
    UsbConfigurationInfo.cs
    UsbInterfaceInfo.cs
    UsbEndpointInfo.cs
    UsbTopologyCollector.cs
```

### 9.1 Snapshot Shape

```csharp
public sealed record PciTopologySnapshot(
    IReadOnlyList<PciTopologyNode> Nodes,
    IReadOnlyList<string> RootNodeIds,
    DeviceTopologyDiagnostics Diagnostics,
    DateTimeOffset GeneratedAt);

public sealed record UsbTopologySnapshot(
    IReadOnlyList<UsbTopologyNode> Nodes,
    IReadOnlyList<string> HostControllerNodeIds,
    IReadOnlyList<UsbCompanionPortLink> CompanionPorts,
    DeviceTopologyDiagnostics Diagnostics,
    DateTimeOffset GeneratedAt);
```

### 9.2 Common Identity

```csharp
public sealed record PnpDeviceIdentity(
    string StableId,
    string InstanceId,
    string DisplayName,
    string DeviceDescription,
    string Manufacturer,
    Guid? ClassGuid,
    Guid? ContainerId,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    IReadOnlyList<string> LocationPaths,
    string Enumerator,
    string Service,
    DeviceNodeStatus Status);
```

### 9.3 Stable Identity Rules

PCI node：

1. PnP Instance ID as primary identity。
2. Verified Location Path + BDF as secondary correlation key。
3. Do not use friendly name as identity。

USB physical device：

1. Controller stable ID + hub path + connection port chain identifies the physical attachment point。
2. PnP Instance ID identifies the current Windows device instance。
3. Container ID correlates composite functions and cross-stack representations。
4. Serial number is useful for correlation but is not universally present or trustworthy。

USB port：

```text
usb-port:{controllerStableId}:{hubPortChain}
```

Refresh selection restoration should first match node stable ID, then physical attachment ID, then nearest existing ancestor。

### 9.4 App Projection

现有 `HwScope.App.Topology.Model.TopologyDocument` 是绘制用 projection，不是领域事实。三个界面使用不同 projection：

```text
PciTopologySnapshot / UsbTopologySnapshot
  -> CompactTopologyAdapter
  -> compact TopologyDocument
  -> main-page CompactTopologyMap

PciTopologySnapshot / UsbTopologySnapshot
  -> FullTopologyVisualAdapter
  -> full TopologyDocument
  -> DeviceTopologyInspectWindow / TopologyCanvas

PciTopologySnapshot / UsbTopologySnapshot
  -> typed diagnostic view model
  -> DeviceTopologyDiagnosticsWindow virtualized tree
```

主页面和 Inspect 可以共享 generic drawing primitives，但必须使用不同的节点可见性、折叠和布局选项。诊断窗口直接绑定 typed view model，不通过 `TopologyDocument.Properties` 反解析领域数据。

## 10. Windows Collection Architecture

### 10.1 Common PnP Layer

建议新增：

```text
HwScope.Core/Windows/Devices/
  ConfigurationManager.cs
  SetupApiDeviceEnumerator.cs
  DevicePropertyReader.cs
  DeviceResourceReader.cs
  DeviceNotificationRegistration.cs
  NativeDeviceModels.cs
```

职责：

- 枚举 present DevNode/interface。
- 读取 Instance ID、parent/child/sibling、Location Paths、Container ID。
- 读取 driver、class、service、hardware IDs、status/problem code。
- 把 native buffer 立即转换为纯 managed record。
- 统一处理 `CR_NO_SUCH_DEVNODE`、设备移除和 property not found。

主要 API：

- `CM_Get_Child` / `CM_Get_Sibling` / `CM_Get_Parent`。
- `CM_Get_Device_ID` / `CM_Locate_DevNode`。
- `CM_Get_DevNode_Property` / `CM_Get_DevNode_Status`。
- `SetupDiGetClassDevs` / `SetupDiEnumDeviceInfo`。
- `SetupDiEnumDeviceInterfaces` / `SetupDiGetDeviceInterfaceDetail`。
- `SetupDiGetDeviceProperty`。
- `CM_Get_First_Log_Conf` / resource descriptor APIs where appropriate。

WMI 只能作为低优先级补充，不能作为树结构主数据源。

### 10.2 PCIe Collector

采集流程：

```text
Enumerate present PCI DevNodes
  -> build PnP parent/child graph
  -> identify PCI roots, bridges, ports and endpoints
  -> read Location Paths / BusNumber / Address
  -> parse and validate BDF
  -> read PCI bus properties exposed by Windows
  -> read resources and driver/PnP status
  -> validate graph, break cycles, emit immutable snapshot
```

地址规则：

- 优先使用非本地化的 device property。
- `Location Information` 只用于显示或 fallback，不应作为唯一解析输入。
- `Location Path` parser 必须接受多段 `PCIROOT(...)#PCI(...)`，并以 fixtures 验证。
- BDF 与 Location Path 冲突时不猜测，保留两者并写 diagnostic。

PCI bus property keys 在不同 Windows/pci.sys/设备上可用性不同。实现必须按字段探测，不因单个 property 不存在而让节点失败。

首版应优先读取 Windows SDK `pciprop.h` 定义的这些 property keys：

| Group | Property keys |
| --- | --- |
| Classification | `DEVPKEY_PciDevice_DeviceType`, `BaseClass`, `SubClass`, `ProgIf` |
| Transaction | `CurrentPayloadSize`, `MaxPayloadSize`, `MaxReadRequestSize` |
| Link | `CurrentLinkSpeed`, `CurrentLinkWidth`, `MaxLinkSpeed`, `MaxLinkWidth`, `ExpressSpecVersion` |
| Interrupt/BAR | `InterruptSupport`, `InterruptMessageMaximum`, `BarTypes` |
| Reliability | `AERCapabilityPresent`, error masks/severity/reporting, `ECRC_Errors` |
| Isolation/virtualization | `SriovSupport`, `AcsSupport`, `AcsCompatibleUpHierarchy`, `AriSupport`, `AtsSupport` |
| Power/link state | `S0WakeupSupported`, `SupportedLinkSubState`, `D3ColdSupport` |
| USB4/tunneling | `UsbDvsecPortType`, `UsbComponentRelation`, `UsbHostRouterName`, `IsTunneledDevice` |

`DEVPKEY_Device_Address` 的 PCI 编码按 Windows contract 解为：

```text
device   = (address >> 16) & 0xffff
function = address & 0xffff
```

随后校验 `device <= 31`、`function <= 7`，并与 `DEVPKEY_Device_BusNumber`、最后一段 PCI Location Path 交叉验证。不要把 Address 直接显示成 device number。

Link speed property 必须同时保存 raw integer 和 normalized display。当前 Windows SDK 头文件中的命名常量落后于现代 PCIe generation，但现代 `pci.sys` 可以返回更高 numeric value。只有在以下条件满足时才把 numeric value `N` 显示为 `PCIe Gen N`：

1. value 在实现明确支持的 generation 范围内；
2. 已用当前 Windows SDK/目标 Windows 版本和真实设备交叉验证；
3. raw value 保留在 diagnostics/JSON；
4. 未知 future value 显示 `Unknown (raw N)`，不能套用当前公式。

### 10.3 USB Collector

建议新增：

```text
HwScope.Core/Windows/Usb/
  UsbHostControllerEnumerator.cs
  UsbHubHandle.cs
  UsbHubIoControl.cs
  UsbDescriptorParser.cs
  UsbPnpCorrelator.cs
  UsbNativeConstants.cs
  UsbNativeModels.cs
```

采集流程：

```text
Enumerate GUID_DEVINTERFACE_USB_HOST_CONTROLLER
  -> open controller interface
  -> IOCTL_USB_GET_ROOT_HUB_NAME
  -> open root hub
  -> query hub information and port count
  -> for each connection index
       -> query connection status / speed / device descriptor
       -> query connector and companion properties
       -> query driver key for PnP correlation
       -> if external hub, query downstream hub name and recurse
       -> read bounded standard descriptors for connected device
  -> correlate physical devices with PnP DevNodes/functions
  -> validate depth/node limits and emit immutable snapshot
```

主要接口：

- `GUID_DEVINTERFACE_USB_HOST_CONTROLLER`。
- `IOCTL_USB_GET_ROOT_HUB_NAME`。
- `IOCTL_USB_GET_NODE_INFORMATION` / `IOCTL_USB_GET_HUB_INFORMATION_EX`。
- `IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX` / `_EX_V2`。
- `IOCTL_USB_GET_NODE_CONNECTION_NAME`。
- `IOCTL_USB_GET_NODE_CONNECTION_DRIVERKEY_NAME`。
- `IOCTL_USB_GET_PORT_CONNECTOR_PROPERTIES`。
- `IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION`。

只允许内部定义的标准只读查询。不得暴露任意 IOCTL、descriptor type、control request 或 buffer length 给 UI/CLI 参数。

### 10.4 Composite Device Correlation

物理 USB device 和 Windows function nodes 分层展示：

```text
Port 2
  M12i Mobile Headphone Amplifier       physical device
    USB Audio 2.0                       interface/function
    HID Consumer Control                interface/function
    HID Vendor Defined                  interface/function
```

关联优先级：

1. Hub IOCTL driver key -> SetupAPI driver registry property。
2. Container ID。
3. Parent/child DevNode relationship。
4. Instance ID base + `MI_xx` pattern as bounded fallback。

fallback 必须标记为 Correlated，不得把无法关联的 function 强行挂到相邻设备。

### 10.5 PCIe/USB Cross-Link

USB Host Controller 通常也是 PCI endpoint。交叉链接使用：

- Host controller PnP Instance ID。
- Location Paths。
- Container ID。
- DevNode parent chain。

成功关联后：

- PCIe USB controller 详情提供“在 USB 树中打开”。
- USB Host Controller 详情提供“在 PCIe 树中打开”。
- link 只保存目标 stable ID，不复制对方字段。

### 10.6 USB4 Boundary

首版规则：

- 枚举 Windows USB stack 暴露的 Host Controller、Hub、Port 和 USB device。
- USB4 dock 下以普通 USB 方式出现的设备正常进入 USB 树。
- PCIe tunneling endpoint 进入 PCIe/PnP 树，但首版不声称知道完整 USB4 router path。
- DisplayPort tunnel 不进入 USB device tree。
- 可以展示 `pciprop.h` 已公开的 USB4 DVSEC/tunneled-device property，但它们只是节点属性，不足以构建完整 USB4 fabric。
- 后续若接入受支持的 USB4 Connection Manager API，应建立独立 USB4 fabric model，不修改 USB 物理端口事实。

### 10.7 Feasibility Findings On Current Machine

2026-07-15 使用本机 Windows 11 SDK `10.0.26100.0` 和当前硬件做了只读 property spike，结果证明首版 PCIe 树和关键 link 字段不需要先开发内核驱动。

当前 Samsung NVMe controller：

```text
DEVPKEY_Device_BusNumber                 3
DEVPKEY_Device_Address                   0       -> device 0, function 0
DEVPKEY_Device_LocationPaths             PCIROOT(0)#PCI(0204)#PCI(0000)
DEVPKEY_PciDevice_CurrentLinkSpeed       4
DEVPKEY_PciDevice_CurrentLinkWidth       4
DEVPKEY_PciDevice_MaxLinkSpeed           4
DEVPKEY_PciDevice_MaxLinkWidth           4
DEVPKEY_PciDevice_CurrentPayloadSize     1       -> 256 bytes
DEVPKEY_PciDevice_MaxPayloadSize         2       -> 512 bytes
DEVPKEY_PciDevice_AERCapabilityPresent   true
```

这些值与用户提供的 HWiNFO 截图中的 `3:0:0`、PCIe 4.0、current/max x4 和 256/512-byte payload 一致。

当前 AMD USB Host Controller 也返回：

```text
BusNumber                                196
Address                                  4       -> device 0, function 4
LocationPath                             PCIROOT(0)#PCI(0801)#PCI(0004)
Current/Max LinkSpeed                    4
Current/Max LinkWidth                    16
BaseClass/SubClass/ProgIf                 0x0c / 0x03 / 0x30 (xHCI)
```

这说明 PCIe collector 可以直接识别并交叉链接 USB xHCI controller。正式实现仍必须在 Intel/AMD、不同 Windows build 和非管理员模式下验证 property availability；本机结果不能被硬编码为通用规则。

## 11. Safety And Robustness

### 11.1 Access Boundary

- 所有操作只读。
- Handle 使用 API 所需的最小权限；某些 hub IOCTL 即使需要 write access 打开 handle，也不得发送改变状态的命令。
- 不接受用户提供 raw path、IOCTL、offset、opcode 或 descriptor request。
- 不默认把完整序列号/device path 写入 crash log。
- JSON/report 导出提供“隐藏敏感标识”选项，默认隐藏序列号和完整 Container/Instance suffix。

### 11.2 Buffer Validation

- 所有 variable-length property 先取得 required size，再设置严格上限。
- 校验 USB descriptor `bLength`、`wTotalLength`、descriptor type 和 remaining buffer。
- 校验 hub port count、tree depth、node count 和字符串 descriptor 长度。
- 检测重复 hub symbolic link、循环 parent、重复 stable ID。
- 非法单节点/单分支不得导致整个快照失败。

建议硬上限：

```text
Maximum total nodes       16,384
Maximum USB hub depth     16
Maximum ports per hub     255
Maximum descriptor bytes  64 KiB per device
Maximum string length     4 KiB
```

超过上限时截断分支并记录 `MalformedResponse`/`LimitExceeded` diagnostic。

### 11.3 Error Model

```text
AccessDenied
DeviceRemoved
InterfaceNotFound
PropertyUnavailable
UnsupportedWindowsVersion
UnsupportedController
IoctlFailed
MalformedResponse
CorrelationFailed
Timeout
Cancelled
LimitExceeded
Unknown
```

UI 区分：

- 不可用：当前数据源未提供。
- 不支持：明确不支持该查询。
- 读取失败：本次查询发生错误，可刷新。
- 已断开：快照中的设备已在刷新/通知后移除。
- 异常：Windows Problem Code 或 USB connection status 表示问题。

## 12. Collection, Concurrency And Refresh

### 12.1 Service Boundary

App 新增按需服务：

```text
HwScope.App/Services/DeviceTopologyService.cs
```

主页面、窗口和 UI components：

```text
HwScope.App/Pages/DeviceTopologyPage.xaml(.cs)
HwScope.App/Pages/DeviceTopology/
  CompactTopologyMap.xaml(.cs)
  CompactTopologyAdapter.cs
  DeviceTopologyWorkspaceViewModel.cs
  PciNodeDetailsView.cs
  UsbNodeDetailsView.cs
  TopologySearchIndex.cs

HwScope.App/Windows/DeviceTopologyDiagnosticsWindow.xaml(.cs)
HwScope.App/Windows/DeviceTopologyInspectWindow.xaml(.cs)
HwScope.App/Windows/SingleInstanceWindowKeys.cs

HwScope.App/Pages/DeviceTopology/
  VirtualizedDiagnosticTree.cs
  DiagnosticTopologyViewModel.cs
  FullTopologyVisualAdapter.cs
```

职责：

- 分别持有最新 PCIe 和 USB snapshot。
- 同类并发请求合并。
- PCIe 与 USB 采集互不阻塞。
- USB 按 Host Controller 分支隔离，单个 controller/hub 超时不拖住其他分支。
- 发布 snapshot、branch progress、error 和 device change event 回 Dispatcher。
- 保留最近成功快照；刷新失败时不清空主页面或已打开窗口。

### 12.2 Preload Policy

首版不把完整拓扑加入 `HardwareInventorySnapshot`：

- PCIe/USB 主页面首次打开时按需加载。
- 启动 preload 继续保持现有硬件摘要速度。
- 后续可评估只预加载轻量 PnP index，但 USB hub port/descriptor 查询仍按需执行。
- 存储页等消费者只保存 topology reference，不触发完整拓扑刷新。

### 12.3 Timeouts

建议初始预算：

```text
PnP/PCIe full snapshot       5 s soft timeout
USB per host controller     3 s soft timeout
USB full snapshot           8 s overall soft timeout
Single descriptor request   1 s soft timeout
```

优先使用 overlapped I/O + `CancelIoEx`。如果特定 host controller driver 仍可能不可取消地阻塞，后续把 USB branch query 放入可终止 worker 进程，领域模型不变。

### 12.4 Hot-Plug

- 使用 `CM_Register_Notification` 监听 device interface/instance changes；WPF 可用 `WM_DEVICECHANGE` 作为兼容 fallback。
- 通知只作为“需要重新枚举”的信号，不携带完整业务事实。
- 300-500 ms debounce 合并 dock/hub/composite device 的通知风暴。
- 首版通知后刷新 USB snapshot；PCIe 对 eGPU/Thunderbolt arrival/removal 也刷新。
- 刷新期间保留旧树并显示“正在更新”。
- 新 snapshot 到达后做 stable-ID diff，恢复 selection/expansion。
- 当前选中设备移除时，显示“设备已断开”和最后快照，用户选择其他节点后再释放该 detached view。

## 13. UI Information Architecture

### 13.1 Three-Surface Decision

确认采用三层 UI，而不是把全部能力放在一个页面或一个独立窗口：

| Surface | Responsibility | Data density | Primary representation |
| --- | --- | --- | --- |
| 主页面 | 快速理解拓扑、选择设备、查看关键字段 | Medium | 紧凑可展开层次图 + 下方详情 |
| 诊断窗口 | 精确目录、空端口、interface/function、raw properties | Highest | HWiNFO/USBTreeView 风格多级树 + 原始信息 |
| Topology Inspect | 全局空间关系、路径高亮、缩放、图层和导出 | Visual | CPU Inspect 风格完整绘图 |

当前主窗口已有 236 px 全局 NavigationView，因此主页面不能再增加一个固定左侧目录树。主页面使用横向占满内容区的层次拓扑图，位于标签页顶部；选择节点后，详情在图下方展开，整体行为与存储页“设备选择 -> 选中设备详情”一致。

诊断窗口和 Inspect 窗口都是 modeless 单实例窗口。它们允许与主页面、存储详情或网络详情并排对照，但不承担主页面的日常导航职责。

三个界面的边界必须保持：

- 主页面不展示全部 raw property、每个空 USB port 或所有 composite function。
- 诊断窗口不追求思维导图式空间布局，优先保证精确层级和原始可追溯性。
- Inspect 不替代诊断窗口；它负责完整绘图、关系高亮、缩放和导出。
- 三者共享 snapshot/detail cache，但各自保存 presentation state。

### 13.2 Launch And Navigation

主页面进入方式：

```text
硬件
  中央处理器 (CPU)
  内存
  显示设备
  存储设备
  网络设备
  总线与端口
```

`总线与端口` 是正常 NavigationView page，Tag 建议为 `device-topology`，由 MainWindow 持有 `DeviceTopologyPage` 单例。主窗口顶部快捷工具栏可以增加 `总线` 快捷入口，但它只导航到同一主页面。

主页面 Header 提供两个明确命令：

```text
[诊断] [拓扑全貌]
```

- `诊断` 打开/激活 `DeviceTopologyDiagnosticsWindow`。
- `拓扑全貌` 打开/激活 `DeviceTopologyInspectWindow`。
- 两个窗口都接收当前 PCIe/USB mode 和当前选中节点。
- 存储、网络、GPU 等详情页的 `在总线拓扑中定位` 默认导航到主页面；用户可随后打开诊断或 Inspect。

两个窗口接入现有单实例基础设施：

```text
SingleInstanceWindowKeys.DeviceTopologyDiagnostics
SingleInstanceWindowKeys.DeviceTopologyInspect
App.SingleInstanceWindows.ShowOrActivate(...)
```

Cross-navigation contract：

```csharp
public interface IDeviceTopologyNavigator
{
    void ShowPage(DeviceTopologyTarget? target = null);
    void ShowDiagnostics(DeviceTopologyTarget? target = null);
    void ShowInspect(DeviceTopologyTarget? target = null);
}

public sealed record DeviceTopologyTarget(
    DeviceTopologyMode Mode,
    string? StableNodeId,
    string? PnpInstanceId = null,
    string? LocationPath = null);
```

如果目标 snapshot 尚未加载，对应 surface 保存 pending target；采集完成后按 stable ID -> PnP Instance ID -> Location Path 的顺序定位、展开全部祖先、选中节点并滚动到可见位置。定位失败时保留目标标识并显示原因，不静默选择相邻设备。

### 13.3 Main Page Structure

主页面沿用 CPU/内存/存储详情页的纵向结构，不新增第二个固定侧栏：

```text
Header
  Title / generated time / node counts
  Refresh / Diagnostics / Topology Inspect / Copy / Save

Tabs
  PCI Express
  USB

Compact Topology Overview
  fixed-height expandable hierarchical map

Selected Node Header
  identity / coordinate / status / key chips

Selected Node Details
  summary sections and notes
```

页面由 MainWindow 持有单例。PCIe 和 USB tab 分别保存 selected node、expanded branches、map viewport 和最后使用的详情位置。

### 13.4 Main Page Desktop Layout

```text
--------------------------------------------------------------------------------
总线与端口                73 个 PCIe 节点 · 15:42:18
                                      [刷新] [诊断] [拓扑全貌] [复制] [保存]

[ PCI Express ] [ USB ]

| 紧凑拓扑概览                                                         [适应] |
|                         [PCI Root 0000]                                     |
|                      ┌──────────┴──────────┐                                 |
|               [Root Port 02.1]      [Root Port 08.1]                        |
|                     │                 ┌────┴────┐                             |
|              [NVMe 03:00.0]      [USB xHCI] [更多 3 个设备]                 |

Samsung NVMe Controller                          03:00.0 · PCIe 4.0 x4 · 正常
Root 0000 > Root Port 02.1 > 03:00.0

| 身份与位置                         | 链路与能力                              |
| 驱动与状态                         | 资源摘要                                |
| 数据说明                                                                     |
--------------------------------------------------------------------------------
```

布局原则：

- Compact map 横向占满 PageHost，不与 NavigationView 形成双侧栏。
- Map 和 selected-node details 是同一页面的上下关系，行为与存储页设备选择区一致。
- 主页面只显示关键字段；raw IDs、完整 descriptors 和全部 property 进入诊断窗口。
- 不在 map 节点内显示超过两行文字或四个 badge。

### 13.5 Compact Map Sizing

建议尺寸：

```text
MinHeight       260
PreferredHeight 320
MaxHeight       420
NodeWidth       148 compact / 176 detailed
NodeHeight      58 compact / 76 detailed
```

- Map 超出边界时使用内部 ScrollViewer；不能无限增高把下方详情推到数屏之外。
- 普通滚轮滚动 map viewport；`Ctrl+Wheel` 才缩放，避免抢占页面滚动。
- 主页面不支持自由拖拽节点和无限画布平移。
- 提供 `适应视图`、放大、缩小；复杂交互放在 Inspect。
- PageHost 宽度 < 760 px 时使用 compact node、隐藏次要 subtitle，仍保留拓扑图；不改成固定左侧树。

### 13.6 Main Page Commands

Header commands：

- `刷新`：刷新当前 tab 对应 snapshot，不刷新 CPU/Memory inventory。
- `诊断`：打开诊断窗口并定位当前节点。
- `拓扑全貌`：打开 Inspect 并高亮当前节点及上游路径。
- `复制`：复制当前节点摘要。
- `保存`：保存当前节点文本报告；完整 JSON 从诊断/Inspect export menu 导出。

Map 内只保留 `适应视图`、zoom 和必要的 branch visibility toggle，不重复 Header commands。

### 13.7 Compact Map Expansion Behavior

主页面的“展开”含义是控制 compact projection 的可见分支，不改变 Core snapshot，也不影响诊断窗口/Inspect 的完整节点集合。

“有挂载设备”统一使用更准确的定义：分支存在 present/connected meaningful descendant。

首次打开默认展开策略：

PCIe：

- 展开 synthetic computer/root bus 和所有通向 present endpoint 的祖先路径。
- 有 endpoint descendant 的 Root Port/Bridge 默认展开。
- 只有 bridge、没有 meaningful endpoint descendant 的分支默认收缩。
- Dummy Function、内部 fabric function 默认不进入 compact projection。
- Windows 未枚举的空物理插槽不生成虚构节点。
- 如果由 cross-link/search 打开，目标全部祖先强制展开。

USB：

- 展开 Host Controller、Root Hub 和所有 connected physical device path。
- 主图默认不逐个展示空 Port；Hub 节点使用 `4 个空闲端口` 一类折叠 badge。
- 用户展开空端口摘要后，才在 compact map 中插入具体 Port leaf。
- external hub 若有已连接下游设备则展开；全空 hub 默认折叠。
- Port number 优先显示为 edge label，减少 `Hub -> Port -> Device` 的额外视觉层级。
- composite interface/function 不进入主图，physical device 节点用 function count badge 表示。

收缩节点必须显示隐藏内容摘要，例如 `3 endpoints`、`4 empty ports`、`5 functions`。用户不应因收缩误以为分支为空。

交互规则：

- 单击 node body：选中并更新下方详情，不改变展开状态。
- 单击 node chevron/count badge：只展开/折叠，不改变当前 detail selection。
- 双击 branch node：展开/折叠；双击 leaf 不执行设备操作。
- 键盘方向键按布局中的 parent/child/sibling 关系移动焦点。
- `Enter` 选中焦点节点；`Space` 展开/折叠 branch。
- Map 节点不能拖动或重新定义父子关系。

刷新时按 stable ID 恢复 expansion。不存在的 expanded node 被移除；新节点遵循当前 tab 的默认展开策略，不继承相邻节点状态。

### 13.8 Diagnostics Tree Virtualization

本节只适用于 `DeviceTopologyDiagnosticsWindow` 的精确目录树。主页面使用 bounded compact map，Inspect 使用 virtualized canvas，二者不复用 tree row 布局。

诊断窗口不直接依赖默认 WPF `TreeView` 承载大量空端口和 function。建议实现：

```text
Immutable topology snapshot
  -> expansion/filter/search projection
  -> diagnostic expansion/filter/search projection
  -> flat VisibleDiagnosticRow list
  -> virtualized ListBox / ItemsControl
```

每个 row 包含：

```csharp
public sealed record VisibleDiagnosticRow(
    string NodeId,
    int Depth,
    bool HasChildren,
    bool IsExpanded,
    SymbolRegular Icon,
    string Label,
    string Coordinate,
    TopologyNodeStatus Status,
    IReadOnlyList<string> Badges);
```

优点：

- `VirtualizingStackPanel` recycling 行为可控。
- 搜索/过滤后保留祖先更简单。
- 展开状态与 domain snapshot 分离。
- 1000+ 空 USB port/PCI function 不需要同时创建视觉树节点。

Row 设计：

- 30-32 px 固定高度。
- 16 px chevron，18 px device icon。
- 主标签单行 ellipsis。
- 右侧只显示一个关键坐标或速度，如 `03:00.0`、`4-1-2`、`10G`。
- Problem/Disconnected 使用 icon + 文本/tooltip，不只依赖颜色。

### 13.9 Selection And Detail Loading

主页面选择节点后，在 compact map 下方更新 grouped details；不打开 flyout，不把详情挤到第二个侧栏。选择必须是 snapshot-local 操作，不重新枚举整棵树。

PCIe：

- 身份与位置、链路与能力、驱动与状态、资源摘要等常规字段已在 snapshot 内，选择后立即呈现。
- 少量昂贵 supplemental property 可按 node cache 懒加载，但不能改变 parent/child 结构。

USB：

- 初始 tree snapshot 读取 controller/hub/port connection、基础 device descriptor 和用于递归的 hub name。
- configuration/interface/endpoint/string/BOS 等深层 descriptor 在首次选中 physical device、展开主页面对应详情组或打开诊断窗口对应 tab 时加载。
- detail load 有独立 cancellation/version guard；快速选择另一节点不会把旧 descriptor 显示到新标题下。
- 成功 detail 按 physical attachment stable ID 缓存；设备断开或 attachment identity 改变后失效。
- 完整 JSON 导出若包含所有 descriptor，必须显式显示批量读取进度并允许取消。

主页面 selected-node header 和诊断窗口右侧 header 始终先显示 snapshot 中已有的 identity/status/breadcrumb。懒加载只影响相应详情组/tab body，不把整个详情区替换成空白 loading page。

三个 surface 通过 `IDeviceTopologyDetailCache` 共享同一 detail task/result。多个界面同时请求同一 attachment 时必须 coalesce，不能重复发送 USB descriptor IOCTL。缓存发布 immutable result，UI 不共享可变 ViewModel。

### 13.10 Main Page Detail Groups

主页面不使用“原始信息”式密集 tabs，而是复用存储详情页的分组 section。默认只展开最重要的前两组，其余组保留标题和一行摘要。

PCIe：

```text
身份与位置
  Device name, type, BDF, vendor/device, location, breadcrumb
链路与能力
  Current/max generation, GT/s, width, payload, ASPM, supported flags
驱动与状态
  Provider, description, version, date, INF, service, capabilities
资源摘要
  IRQ/MSI, memory ranges, I/O ranges, bridge bus ranges
数据说明
  unavailable/derived/source notes and diagnostics entry point
```

USB：

```text
连接与位置
  Device name, port chain, connection state, VID/PID, breadcrumb
速度与能力
  Device maximum, port capability, negotiated speed, USB version
供电与描述符摘要
  Requested/available power, device/configuration/BOS summary
驱动与功能
  Provider/version/status and composite function count
数据说明
  unavailable/derived/source notes and diagnostics entry point
```

节点类型决定可见组：

- Empty port 只显示端口能力、状态和位置，不显示设备描述符组。
- Hub 增加 hub characteristics 和 downstream port summary。
- Interface/function 不重复显示物理 device connection speed，改为提供“父设备”跳转。
- 若整组字段不可用，显示明确的 unavailable 原因，不保留空白 section。

### 13.11 Diagnostics Window Design

诊断窗口面向精确核对与故障定位，默认尺寸建议 `1180 x 760`，最小尺寸 `980 x 640`：

```text
--------------------------------------------------------------------------------
PCIe 诊断                         [搜索] [筛选] [刷新] [在拓扑全貌中定位] [导出]
[ PCI Express ] [ USB ]
--------------------------------------------------------------------------------
精确目录树                         | 选中节点 identity / coordinate / status
Computer                           | [概览] [能力/描述符] [资源/接口] [驱动] [原始]
  PCI Root                         | structured fields
    Root Port                      | source / availability / diagnostics
      NVMe                         | bounded raw property or descriptor view
--------------------------------------------------------------------------------
```

- 左侧 splitter 初始宽度 360 px，可调范围 280-520 px；窗口宽度不足时优先压缩右侧字段列，不自动隐藏目录树。
- PCIe 精确树保留 Root/Bridge/Endpoint/Function 和可验证的 PnP parent；USB 精确树保留 Controller/Hub/Port/Device/Function，包含空端口。
- 搜索、筛选、全部展开/折叠、展开当前路径只影响诊断窗口 presentation state。
- 右侧使用完整 tabs：PCIe 为 `概览 / 链路与能力 / 资源 / 驱动 / 原始`；USB 为 `概览 / 描述符 / 接口与端点 / 驱动 / 原始`。
- 原始 tab 显示 bounded raw property、descriptor hex、来源、Win32/CONFIGRET/IOCTL diagnostic；不提供任意地址、任意 IOCTL 或 control transfer 输入框。
- 顶部统计来自完整 snapshot，不受可见筛选影响；筛选结果数单独显示。
- 命令包括复制字段、复制路径、保存节点报告、导出完整 JSON、在另一总线 cross-link 中定位、在 Inspect 中定位。

PCIe header summary 示例：

```text
4 roots · 27 bridges · 42 endpoints · 1 problem
```

USB header summary 示例：

```text
4 controllers · 7 hubs · 38 ports · 12 connected · 1 problem
```

首版不提供 Reset/Eject/Disable 等有副作用命令。

### 13.12 Topology Inspect Window Design

Inspect 延续 CPU Inspect 的窗口、画布和工具栏交互，但使用完整 PCIe/USB visual projection：

```text
--------------------------------------------------------------------------------
总线拓扑全貌  [PCI Express | USB] [图层] [适应] [-] 100% [+] [路径高亮] [导出]
--------------------------------------------------------------------------------

                      full virtualized topology canvas
                  pan / zoom / minimap / selected path

--------------------------------------------------------------------------------
selected node summary / legend / snapshot time
```

- 默认尺寸、TitleBar、zoom controls、minimap、导出菜单尽量复用 CPU Inspect 既有模式。
- PCIe 默认 top-down 展示 root -> bridge -> endpoint；USB 默认 controller -> hub -> connected device，空端口通过图层开关显示。
- 支持平移、滚轮缩放、适应全部、适应选中路径、minimap、节点搜索和上游路径高亮。
- 图层至少包含 coordinates、link/speed、status、empty ports、functions、cross-links；改变图层不改变 Core snapshot。
- 节点位置由确定性 layout 生成，首版不保存用户拖拽形成的“事实拓扑”。可以允许临时拖动画布，不允许通过拖动节点改变 parent/child。
- 大树只 materialize viewport 附近的 visuals；edge/node drawing 不创建与节点数同量的重型 WPF controls。
- 导出支持完整 JSON 和当前视图 PNG；PNG 必须带 snapshot time、mode、legend 与 unavailable note。

Inspect 是“全貌绘图”，不是放大版主页面。主页面强调当前分支和关键字段；Inspect 可以显示全树、图层、跨链路和全局空间关系。

### 13.13 Shared State And Cross-Surface Navigation

Core 层为每个 mode 发布单一 versioned immutable snapshot；三个 surface 订阅同一 snapshot service，不得各自启动枚举器。刷新当前 mode 时，所有已打开 surface 接收同一新版本。

共享：

- immutable PCIe/USB snapshot、generation/version、stale/error state；
- detail task/result cache 和 invalidation；
- stable identity/correlation、cross-link 和 text/JSON formatter；
- refresh coalescing、timeout、hot-plug dirty/debounce 状态。

不共享：

- 当前 tab/mode、selection、expanded branches；
- map viewport、zoom、pan、diagnostic splitter 和 active detail tab；
- search/filter、visible layers 和临时 detached-node presentation。

打开另一个 surface 时只传递一次当前 mode/node target。之后不做持续双向 selection sync，避免用户在两个窗口对照时焦点互相跳动。用户通过明确的 `在诊断中定位`、`在拓扑全貌中定位`、`在主页面中定位` 命令再次同步目标。

目标定位顺序固定为 stable ID -> PnP Instance ID -> Location Path。命中后只改变目标 surface 的 presentation state：主页面展开 compact ancestors，诊断窗口展开 exact ancestors，Inspect 高亮并适应目标路径。

## 14. Visual Language

延续 HwScope 当前设计：

- 主页面使用 MainWindow 既有 title/content rhythm，不创建内嵌 TitleBar；两个独立窗口使用现有 44 px custom TitleBar。
- detail identity 18-20 px；section title 14-15 px；field 13 px；不按 viewport width 缩放字号。
- Compact map、诊断树和详情面板使用全宽分区及细分隔线，不把每个 section 做成浮动卡片。
- Corner radius 最大 7-8 px，仅用于搜索框、tabs、状态 chip 和必要容器。
- 使用项目现有 WPF-UI `SymbolIcon`；只有项目正式引入统一 Lucide 资源后才使用 Lucide，不手绘设备 SVG。
- PCI root/bridge/endpoint、USB controller/hub/port/device/function 使用不同图标和文字，不仅靠颜色。
- 状态色复用 `HwScopeStatusGood/Caution/Critical/Unknown/Info`。
- 新增 topology accent token 时必须同时提供 light/dark 值，不能在 control code-behind 硬编码 RGB。
- 原始 ID/descriptor hex 使用 `Cascadia Mono, Consolas`。
- 长 Instance ID、Location Path 默认换行或中间省略，旁边提供 icon-only copy button 和 tooltip。
- Main map node、Inspect node 和 diagnostic row 保持相同的类型 icon、状态 token 和术语，但允许不同信息密度。

## 15. Loading, Empty And Error States

### Initial Loading

- 主页面先显示 Header、PCIe/USB tabs 和固定高度 map skeleton；已缓存 snapshot 可立即显示并标记“正在更新”。
- 诊断/Inspect 窗口先显示 window shell 和模式 tabs，不等待采集结束再创建窗口。
- 主页面 map、诊断 tree 和 Inspect canvas 都显示当前 branch progress，如“正在读取 USB Host Controller 2/4”。
- 已完成 controller/root 可以渐进显示，但 snapshot 发布必须保持不可变版本。
- 不用全局 `Mouse.OverrideCursor` 锁住整个主窗口。

### No Devices

PCIe：

```text
Windows 没有返回可用的 PCI/PCIe 设备节点。
```

USB：

```text
未发现 USB Host Controller，或当前环境未公开 USB controller interface。
```

### Partial Failure

- 三个 surface 都继续显示成功分支，不因单个 controller/root 失败清空整个 mode。
- 主页面在对应 branch 显示 problem badge；诊断树在失败 controller/root 下显示 synthetic error node；Inspect 显示错误节点/断边图例。
- Detail/diagnostics 给出错误分类、原生错误码、数据源和重试当前分支。
- AccessDenied 不等于 Unsupported，Unsupported 不等于设备异常。

### Stale Data

刷新失败且存在旧 snapshot：

```text
正在显示 15:42:18 的上次成功结果；本次刷新失败：...
```

Copy/Save 默认导出当前显示的旧 snapshot，并写入 stale/error note。三个 surface 对同一 snapshot version 显示一致的 stale/generated time。

## 16. Reporting And Export

### Text Report

当前节点 report 包含：

```text
Topology Path
Identity
Connection / Link
Capabilities
Resources or Descriptors
Driver And PnP
Diagnostics
Generated At
```

### JSON

- 使用 schema version。
- 导出完整 typed snapshot，不导出 App view records。
- 默认 `redactSensitiveIds = true`。
- 保留 source、availability、derived flag 和 diagnostics。
- 字段新增保持向后兼容；breaking change 提升 schema major。

### Visual Export

- Inspect 通过 `FullTopologyVisualAdapter` 将 PCIe/USB snapshot 投影到完整 `TopologyDocument`，导出当前视图 PNG。
- Root/Bridge 或 Controller/Hub 作为 group/branch，Endpoint/Device 作为 node，Link/Port path 作为 edge。
- PNG 导出遵循当前 layer visibility 和 path highlight，但不得把隐藏内容误报为不存在。
- 主页面 compact map 不承担完整拓扑图片导出；其 `保存` 默认生成选中节点文本报告。
- Visual Map 是辅助理解视图，诊断窗口的 exact tree/raw fields 是精确核对入口，Core typed snapshot 是事实来源。

## 17. Performance Requirements

- 主页面导航和独立 window shell 在 100 ms 内可交互，硬件枚举不阻塞 UI thread。
- 常见桌面 PCIe snapshot 目标 1 秒内完成，5 秒 soft timeout。
- 常见 USB snapshot 目标 2 秒内完成，单 controller 不超过 3 秒 soft timeout。
- 10,000 domain nodes 不允许一次性创建 10,000 WPF TreeViewItem、map node controls 或 canvas controls。
- 诊断搜索/展开、compact projection 和 Inspect layer projection 在常见 2,000 节点 snapshot 上目标低于 50 ms。
- 切换 PCIe/USB 模式不重新采集已有新鲜 snapshot。
- 三个 surface 同时打开仍只执行一次同 mode 枚举和一次同 attachment detail load。
- 独立窗口最小化/隐藏时不自动轮询；notification 只标记 dirty 或按策略刷新。
- Inspect 平移/缩放目标保持 60 FPS；大树允许降低 edge decoration，但不能隐藏选中路径或问题状态。

## 18. Accessibility

- Compact map 按 parent/child/sibling 关系支持方向键导航，`Enter` 选择，`Space` 展开/折叠。
- 诊断 tree 支持 Up/Down、Left/Right expand/collapse、Home/End、Enter 打开详情。
- Inspect 支持键盘平移、`+/-` 缩放、适应视图和跳转选中节点；不能只有鼠标才能定位节点。
- Search 支持 `F3` / `Shift+F3` 切换命中。
- 状态同时使用 icon、文本和颜色。
- map nodes/edges、tree rows、splitter、tabs、zoom 和 copy buttons 有 AutomationName；edge 关系可由节点 AutomationHelpText 读取。
- tooltip 不承载唯一重要信息。
- Detail field 支持文本选择或单字段复制。
- 三个 surface 在 200% DPI、中文/英文长名称、超长路径下无重叠；主页面 compact map 仍保留下方详情入口。

## 19. Test Strategy

### 19.1 Pure Unit Tests

PnP/PCIe：

- Parent/child graph construction。
- Missing parent and cycle handling。
- Multi-function devices。
- BDF and Location Path parser。
- BusNumber/Address conflict diagnostic。
- Bridge/root/endpoint classification。
- Link generation/GT/s formatting。
- Resource range formatting。
- Stable ID and refresh selection restoration。

USB：

- Hub/port recursive tree construction。
- Empty/connected/failed connection states。
- External hub recursion and duplicate hub detection。
- USB device/config/interface/endpoint descriptor parsing。
- Short buffer、zero `bLength`、oversized `wTotalLength`、unknown descriptor。
- USB 2 vs USB 3 `bMaxPower` units。
- Composite function correlation。
- Companion port mapping。
- Port chain formatting and stable ID。

Service/UI projection：

- Same-mode request coalescing。
- Three-surface snapshot/detail sharing without duplicate enumeration。
- PCIe/USB isolation。
- Per-controller timeout and late result guard。
- Hot-plug debounce and snapshot diff。
- Search/filter retains ancestors。
- Expand state restoration。
- Compact projection default expansion and hidden-count badges。
- Target localization fallback and one-shot cross-surface handoff。
- Full visual projection layer toggles and selected-path highlighting。
- Redacted JSON export。

### 19.2 Native Boundary Tests

通过 synthetic buffers 测试：

- SetupAPI multi-string/property buffer。
- USB node connection structures for x86/x64 layout where applicable。
- Hub name/driver key variable-length buffers。
- Descriptor boundary and alignment。
- Win32/CONFIGRET error mapping。

不要求测试对真实设备发送写命令。

### 19.3 Hardware Matrix

至少覆盖：

```text
Desktop AMD and Intel platforms
Laptop with integrated USB controllers
Single and multi-root PCIe systems
NVMe, discrete GPU, integrated GPU, PCIe NIC
Multi-function PCI device
USB 2 device on USB 3 controller
USB 3 5/10/20 Gbps device where available
External USB 2 hub and SuperSpeed hub
USB composite audio/HID device
USB mass storage
Bluetooth integrated USB device
USB-C companion ports
USB4/Thunderbolt dock, best-effort boundary validation
eGPU, if available
Hyper-V/VMware virtual machine
Administrator and standard-user modes
Light/dark theme, 100/150/200% DPI
```

每类设备核对：

- 与 Device Manager Location Path/PnP identity 一致。
- 与 USB Device Tree Viewer 的 controller/hub/port chain 一致。
- 与 HWiNFO 可见的 BDF、link、driver 字段交叉验证。
- Unsupported/unavailable 不被显示为 0 或故障。
- 热插拔不会串设备或留下错误 selection。
- Copy/JSON 与 UI 值和来源一致。

### 19.4 UI Automation

- 主页面初次进入、PCIe/USB tab 切换和紧凑图默认展开。
- 主页面选中节点后，下方 grouped details 与 breadcrumb 同步。
- 从主页面打开/激活两个单实例窗口并定位当前节点。
- 搜索 VID/PID、BDF 和名称。
- 诊断树展开/折叠、选择、breadcrumb、USB 空端口筛选和 raw tab。
- Inspect zoom/pan/fit/layer/path highlight 和 PNG export。
- 快速切换 PCIe/USB mode、最小化独立窗口再恢复。
- 刷新期间切换选择和模式。
- 模拟 snapshot 中节点移除。
- 验证三 surface 同时打开不触发重复采集，且 selection/expansion 不互相抢占。
- MainWindow 最小内容宽度、两个窗口最小宽度、长字段、浅色/深色和 100/150/200% DPI 截图检查。

## 20. Implementation Milestones

### Milestone 0: Fixtures And Test Foundation

- 建立 PnP property、PCI Location Path 和 USB descriptor synthetic fixtures。
- 建立 graph/parser/error mapping tests。
- 不依赖真实硬件写操作。

### Milestone 1: Common PnP Substrate

- 实现 Configuration Manager / SetupAPI typed wrapper。
- 枚举 present devices、parent/child、identity、driver、status、location。
- 完成 buffer 上限、错误分类和纯 managed snapshot 转换。

### Milestone 2: PCIe Identity Tree

- 构建 roots/bridges/endpoints/functions。
- 展示 BDF、IDs、class、Location Path、driver 和 status。
- 完成 text/JSON formatter 和 CLI diagnostic prototype。

验收：树结构与 Device Manager/HWiNFO 的主要 parent path 一致，API 失败可局部降级。

### Milestone 3: PCIe Link And Resources

- 探测 Windows 可用的 PCI bus properties。
- 接入 current/max link、payload/ASPM 等真实可得字段。
- 接入 IRQ/memory/I/O resources。
- 明确 unsupported property，不引入 raw config access。

### Milestone 4: USB Controller/Hub/Port Tree

- 枚举 Host Controller 和 Root Hub。
- 递归枚举 hub ports、空端口和 external hub。
- 显示 connection status、port chain、speed layers 和 connector/companion。
- 按 controller 隔离超时和错误。

### Milestone 5: USB Descriptors And PnP Correlation

- 安全解析 device/config/interface/endpoint/string descriptors。
- 关联 physical device、composite functions、driver 和 Container ID。
- 增加 descriptor fixtures 和 malformed coverage。

### Milestone 6: Three UI Surfaces

- 新增 MainWindow `DeviceTopologyPage`，提供 `PCI Express` / `USB` tabs、bounded `CompactTopologyMap` 和下方 grouped details。
- 实现 present/connected meaningful branch 默认展开、空分支默认收缩、hidden-count badge 和 selected-node detail loading。
- 新增单实例 `DeviceTopologyDiagnosticsWindow` 与 `SingleInstanceWindowKeys.DeviceTopologyDiagnostics`。
- 诊断窗口实现 virtualized exact tree、GridSplitter、完整 details/raw tabs、search/filter/copy/save。
- 新增单实例 `DeviceTopologyInspectWindow` 与 `SingleInstanceWindowKeys.DeviceTopologyInspect`。
- Inspect 实现 full visual adapter、确定性布局、zoom/pan/fit/layers/path highlight 和基础导出。
- 三个 surface 共享 snapshot/detail cache，完成 pending target、单次 target handoff 和 PCIe/USB cross-navigation。

### Milestone 7: Hot-Plug And Diagnostics

- 接入 device notifications 和 debounce。
- 实现 snapshot diff、detached selected device、stale snapshot state。
- 完善 branch diagnostics 和 retry。

### Milestone 8: Inspect Hardening And Cross-Module Links

- 完善 PCIe/USB full visual projection、viewport virtualization、minimap、图层和大树布局性能。
- 在存储、网络、GPU 详情中提供“在总线拓扑中定位”。
- 完成 PNG/JSON visual export、snapshot metadata、legend 和 unavailable note。
- 完成主页面、诊断窗口、Inspect 之间显式定位命令，不引入持续双向 selection sync。

### Milestone 9: Hardware Matrix Completion

- 完成 AMD/Intel、desktop/laptop、hub/composite/USB-C/USB4 best-effort 矩阵。
- 标记 Windows 版本和 controller-specific limitations。
- 只有通过真实设备验证的字段才进入 README “已具备”。

## 21. Acceptance Criteria

### Architecture

- PCIe 与 USB 有独立 typed snapshot，UI 不解析 native buffers。
- SetupAPI/CM 和 USB IOCTL 全部位于 `HwScope.Core.Windows`。
- Window/UI components 不直接调用 P/Invoke/WMI。
- 主页面、诊断窗口和 Inspect 订阅同一 versioned immutable snapshot；同 mode 同时打开只枚举一次。
- 三个 surface 复用同一 detail cache；同 attachment 的深层 descriptor 请求会合并。
- `TopologyDocument`/`TopologyCanvas` 只是 compact/full visual projection，不是事实存储。
- 完整 USB 枚举不进入启动 preload。

### PCIe

- 能展示 Root/Bridge/Endpoint/Function 树。
- 常见 endpoint 能显示 BDF、IDs、class、location、driver 和 status。
- link 字段只有在真实数据源可用时展示，unknown 不伪造成 Gen/x 值。
- resources 和 Problem Code 可追溯。
- 与 HWiNFO/Device Manager 的路径交叉验证通过。

### USB

- 能展示 Controller/Root Hub/Port/Hub/Device 树，并可显示空端口。
- external hub 能递归展开且有深度/循环保护。
- 支持 device/config/interface/endpoint 标准 descriptor。
- device maximum、port capability 和 connection speed 不混淆。
- composite functions 正确归属于 physical device 或明确显示未关联。
- 与 USB Device Tree Viewer 的 port chain 交叉验证通过。

### UX

- MainWindow `总线与端口` 页面包含独立 `PCI Express` / `USB` tabs，不增加第二个固定侧栏。
- 主页面 compact map 对 meaningful connected branch 默认展开，对空/无 endpoint 分支默认收缩，并明确显示隐藏数量。
- 选择 compact map 节点后，下方显示 SSD-detail-style grouped details；PCIe 常用字段立即显示，USB 深层 descriptor 可取消地懒加载。
- 诊断窗口提供包含空端口和 function 的 exact virtualized tree、搜索/筛选和 raw information。
- Inspect 窗口提供完整绘图、zoom/pan/fit/layers/path highlight 和导出。
- 两个独立窗口均为单实例，能接收主页面当前 mode/node target，且不持续抢占彼此 selection。
- 2,000+ 节点诊断树保持可滚动和搜索，Inspect 大树保持可导航，不冻结 UI。
- Search/filter/target localization 保留并展开祖先路径。
- 刷新/热插拔后 selection 不串到其他设备。
- 主页面在 MainWindow 最小内容宽度下仍显示可操作 compact map 和下方详情；两个窗口在 980 px 最小宽度和 200% DPI 下无重叠。
- Copy/Save/JSON 包含生成时间、来源和诊断。

### Safety

- 不存在 raw PCI config、任意 IOCTL 或 USB control request 输入面。
- 所有 native buffers 有长度和数量上限。
- 单 controller/branch 失败不影响其他树。
- 默认日志和 JSON 不泄露完整敏感标识。

## 22. Risks And Mitigations

### PCIe Properties Are Not Uniform

风险：Windows/pci.sys/设备驱动不一定公开 HWiNFO 可见的全部 PCIe capability。

缓解：按字段探测、保留 unavailable；先交付真实树和身份，不以 raw config driver 作为首版前置条件。

### PnP Tree Is Not A Physical Schematic

风险：把 DevNode parent 当作主板物理走线可能误导。

缓解：窗口说明明确这是“Windows 设备拓扑”；显示 Location Path/BDF；不承诺物理插槽映射。

### USB Companion And Type-C Mapping

风险：USB 2/SuperSpeed/USB4 对同一物理 connector 的映射不完整。

缓解：只使用 connector property 报告的 companion link；未知时分别展示，不按端口号猜测。

### Blocking Controller Drivers

风险：某些 hub/controller IOCTL 阻塞。

缓解：per-controller isolation、overlapped I/O、CancelIoEx、soft timeout；必要时升级为 worker process。

### Descriptor Trust Boundary

风险：设备返回 malformed descriptor 导致越界、循环或大分配。

缓解：bounded parser、fixture fuzz cases、节点/深度/长度上限、单设备失败隔离。

### UI Scale

风险：默认 WPF TreeView 或重型 node controls 在大量空端口/function 上创建过多视觉元素。

缓解：诊断树使用 flat visible-row projection + recycling virtualization；compact map 严格限制可见分支；Inspect 使用 viewport virtualization；详情按选择懒绑定。

### Three-Surface Duplication

风险：主页面、诊断窗口和 Inspect 分别实现采集、字段格式化或选择同步，造成重复 IOCTL、结果不一致和窗口焦点跳动。

缓解：Core 只发布单一 immutable snapshot/detail cache；formatter 和 identity resolver 共享；surface 只拥有 presentation state；跨 surface 定位由显式 one-shot target 完成。

### Compact Map Becomes A Second Inspect

风险：持续向主页面节点增加字段、空端口和画布操作，导致页面过高、日常浏览复杂，并与 Inspect 重复。

缓解：主图高度和节点信息密度固定，默认只投影 meaningful branches；全树、图层、自由缩放和图片导出只进入 Inspect；raw fields 只进入诊断窗口。

## 23. Recommended First Delivery

首个可发布版本建议严格限定为：

1. Common PnP substrate。
2. PCIe identity/tree/BDF/driver/status，link 字段 best effort。
3. USB controller/hub/port/device tree，包含空端口切换。
4. 标准 device/config/interface/endpoint descriptors。
5. MainWindow `总线与端口` 双 tab、compact expandable map 和 selected-node grouped details。
6. 单实例诊断窗口：virtualized exact tree、完整 detail/raw tabs、search/filter/copy/save。
7. 单实例 Inspect 窗口：full visual map、zoom/pan/fit/layers/path highlight 和 PNG/JSON export。
8. 三 surface 共享 snapshot/detail cache、显式 cross-surface target、USB hot-plug debounce 和 selection restoration。
9. Synthetic parser/projection tests + 当前机器与 HWiNFO/USB Device Tree Viewer 的交叉验证。

不要让 raw PCI config driver、USB4 fabric、设备控制操作或可编辑自由布局阻塞这个版本。首版价值来自“真实、可追溯、不会伪造”的共享拓扑事实，以及三个职责明确的查看入口，而不是字段数量最大化。
