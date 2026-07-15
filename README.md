# HwScope

HwScope 是一个 Windows 本地硬件工具箱项目，目标是在一个程序里逐步整合硬件摘要、CPU-Z 类详情、传感器、跑分、压力测试和稳定性查询。

当前主线版本已具备：

- WPF 图形界面，基于 WPF-UI / Fluent 风格，包含传统桌面应用式标题栏菜单、左侧导航和 HWiNFO 风格图标+文字快捷工具栏。
- 首页硬件配置摘要，支持卡片视图和列表视图。
- CPU 详情页，展示身份、规格、频率、拓扑、缓存、核心映射、指令集和平台上下文。
- 内存 / SPD 详情页，展示运行态概览、模块选择、WMI/SMBIOS 模块详情、位宽/电压字段和后续 SPD/时序占位。
- 存储设备详情页，按物理磁盘展示身份、固件、序列号、总线、扇区、卷/分区、健康状态、温度、寿命和 SMART / Health 属性。
- 启动期硬件预加载窗口，先建立共享硬件信息库，再由概览页、CPU 页和内存跑分窗口复用同一份快照。
- Windows `GetLogicalProcessorInformationEx` 拓扑采集，提供真实 package/core/thread、CPU group、NUMA、缓存共享和 core-to-logical-processor mapping。
- CPU topology Inspect 窗口，包含 raw report 和绘制版 Visual Map。
- CLI 硬件摘要输出，支持文本、JSON 和复制到剪贴板。
- 独立内存跑分窗口，界面参考 AIDA64 Cache & Memory Benchmark。
- C++ native 内存跑分 worker，当前测量 Memory/L1/L2/L3 Read / Write / Copy / Latency，并支持 topology-aware 多线程 Memory Read / Write / Copy。
- 独立存储跑分窗口和 C++ native `storagebench.exe`，支持 CrystalDiskMark 风格的四行 Read / Write / Mix 文件级测试、写入预算、取消和残留文件安全清理。
- JSON 驱动的主题配置，支持跟随系统、浅色、深色和 Mica 开关。
- 应用图标资源已接入 `HwScope.App`，用于窗口、任务栏和可执行文件图标。

## 项目结构

```text
HwScope.sln
src/
  HwScope.App/
    WPF GUI 入口，启动预加载窗口、主窗口、应用图标资源、硬件摘要页、CPU/内存/存储详情页、主题系统、内存/存储跑分窗口

  HwScope.Cli/
    命令行入口，复用 HwScope.Core 的硬件采集和跑分能力

  HwScope.Core/
    硬件采集、共享 hardware inventory、CPU/内存/存储领域模型、Windows topology/storage API、格式化、benchmark runner 和 native worker 调用

  HwScope.Core.Tests/
    Core 单元测试，当前覆盖 NVMe/ATA parser、存储跑分规划/协议/session 清理、storage descriptor 边界、soft timeout、字段来源合并、bus 格式化和报告格式化

  HwScope.Native.MemoryBench/
    C++ 内存跑分 worker，输出 JSON / progress JSON 给 HwScope.Core 解析，CSV 仅保留为手动兼容格式

  HwScope.Native.StorageBench/
    C++ 文件级存储跑分 worker，使用 overlapped I/O、create-new 测试文件、progress JSON 和有界写入预算

docs/
  project-architecture.md
  cpu-detail-page-design.md
  cpu-detail-implementation-plan.md
  cpu-stage2-topology-plan.md
  cpu-topology-visualization-plan.md
  pcie-usb-topology-design.md
  theme-system.md
  memory-benchmark-design.md
  memory-cache-benchmark-implementation-plan.md
  memory-spd-detail-page-design.md
  memory-spd-detail-implementation-plan.md
  storage-detail-page-design.md
  storage-detail-implementation-plan.md
  storage-benchmark-design.md
```

## 运行

GUI：

```powershell
dotnet run --project .\src\HwScope.App\HwScope.App.csproj
```

GUI 启动时会申请管理员权限，为后续底层硬件能力预留运行条件。用户取消或提权失败时会显示可关闭提示，并继续以普通权限运行；部分底层硬件信息可能缺失。随后应用显示硬件预加载窗口并建立共享硬件信息库。

本地 WPF 开发和 UI Automation 可以在启动进程前设置 `HWSCOPE_SKIP_ELEVATION=1` 跳过 UAC。该开关只用于开发验证，不代表普通权限下所有底层硬件能力都可用，也不应写入生产环境的全局配置。

CLI 硬件摘要：

```powershell
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- --json
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- --copy
```

CLI 内存跑分：

```powershell
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- benchmark memory
```

CLI 存储跑分：

```powershell
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- benchmark storage --drive C: --quick
```

## CPU 详情页

GUI 中可以通过左侧导航 `硬件 -> 中央处理器 (CPU)` 打开 CPU 详情页。

CPU 详情页默认消费启动期共享硬件快照；用户点击刷新时会触发全局硬件 inventory 刷新，并让概览页、CPU 页等订阅者同步到新快照。

当前 CPU 模块采用分层数据源：

- WMI：CPU 名称、规格、频率、主板、BIOS、内存和显卡上下文。
- Windows API：`GetLogicalProcessorInformationEx(RelationAll)` 提供 OS 视角的拓扑、NUMA、CPU group、缓存和核心映射。
- 本地型号映射：作为 code name、工艺、TDP、部分指令集等字段的 fallback，并在页面上标注来源。

已实现：

- 异步刷新，不阻塞主窗口。
- 字段级来源标记：`WMI`、`API`、`映射`、`推导`、`待接入`。
- 复制和保存 `.txt` CPU 报告。
- 缓存行展示容量、路数、line size 和 shared logical processor count。
- 核心映射展示每个 physical core 对应的 processor group/mask。
- 缓存 Inspect 窗口，包含 raw topology report 和绘制版 Visual Map。
- 通用 topology drawing skeleton，CPU topology visual adapter 已接入，后续可复用于 PCIe topology。

后续重点：

- 抽出 topology selection/highlight/details 服务，完善 Visual Map 交互。
- 增加 topology PNG / JSON 导出。
- native CPUID worker，用于 raw family/model/stepping 和真实 feature flags。
- L3 / CCD / V-Cache 洞察，作为启发式信息明确标注。
- 实时频率、传感器、电压、温度和功耗。

详细设计见：

- [docs/cpu-detail-page-design.md](docs/cpu-detail-page-design.md)
- [docs/cpu-detail-implementation-plan.md](docs/cpu-detail-implementation-plan.md)
- [docs/cpu-stage2-topology-plan.md](docs/cpu-stage2-topology-plan.md)
- [docs/cpu-topology-visualization-plan.md](docs/cpu-topology-visualization-plan.md)
- [docs/hardware-preload-design.md](docs/hardware-preload-design.md)

## 内存 / SPD 详情页

GUI 中可以通过左侧导航 `硬件 -> 内存` 或顶部快捷工具栏 `内存` 打开内存 / SPD 详情页。

当前页面默认消费启动期共享硬件快照，使用 Windows `Win32_PhysicalMemory` / SMBIOS 字段展示：

- 总容量、内存类型、模块数量、容量布局和配置速率。
- 每根模块的插槽/位置、容量、模块类型、制造商、Part Number、序列号、Data Width、Total Width 和 WMI 暴露的电压字段。
- 字段级来源标记：`WMI`、`推导`、`待接入`。
- 复制和保存 `.txt` 内存 / SPD 报告。

当前没有 SPD 读取或解析实现。JEDEC / XMP / EXPO 时序、DRAM 制造商、生产周次/年份、rank / bank 组织和 DDR5 feature bits 固定显示 `SPD 读取暂未实现`；当前运行态 CL/tRCD/tRP/tRAS 仍显示内存控制器读取占位。此前的 native worker、离线 parser、fixture 和 Core JSON provider 已移除。

真实 SPD EEPROM/SPD Hub 读取和运行态内存控制器访问通常需要受控内核驱动或经过验证的厂商接口。该工作流当前处于搁置状态，未来重启前必须先确定驱动安全模型、平台支持矩阵、签名/分发方式和故障隔离策略。

详细设计见：

- [docs/memory-spd-detail-page-design.md](docs/memory-spd-detail-page-design.md)
- [docs/memory-spd-detail-implementation-plan.md](docs/memory-spd-detail-implementation-plan.md)

## 存储设备详情页

GUI 中可以通过左侧导航 `硬件 -> 存储设备` 打开独立详情页。页面以物理磁盘为选择单位，不把盘符当成设备身份。

当前已实现：

- 启动 inventory 中扩展物理磁盘编号、设备路径、PnP ID、固件、序列号、扇区和 SCSI 地址字段。
- Windows Storage API 读取设备 descriptor、真实 bus type、logical/physical sector size 和 TRIM 状态。
- `MSFT_Partition` / `MSFT_Volume` 物理磁盘、GPT/MBR 分区、无盘符卷和盘符映射；页面按物理分区展示类型、容量、offset、卷信息和可用空间。
- NVMe 标准 SMART / Health Information log page 0x02 读取。
- NVMe Critical Warning、综合温度、备用空间、Percentage Used、128-bit 读写/命令/通电/错误计数解析。
- 基于标准字段的可解释健康状态和剩余寿命；页面同时展示原始值和字段来源。
- ATA SMART overall status、attributes/threshold parser、sector checksum、保守健康判定和 Windows SMART/ATA pass-through provider。
- 设备级异步缓存、同设备请求合并、跨设备隔离、5 秒 soft timeout、切换 stale-result guard 和页面重入恢复；支持复制和保存 `.txt` 报告。
- CLI：

```powershell
dotnet run --project .\src\HwScope.Cli -- storage list
dotnet run --project .\src\HwScope.Cli -- storage --disk 0
dotnet run --project .\src\HwScope.Cli -- storage --disk 0 --json
```

`storage list` 使用轻量 Windows Storage descriptor 查询真实 bus type，查询不可用时才回退到 WMI `InterfaceType`；它不会触发 NVMe Health 或 ATA SMART 读取。

截至 2026-07-15，存储详情相关的 33 项 Core 测试通过。本机 NVMe 路径已使用真实 Samsung PM9F1 验证。ATA provider 已有 fixture tests，但仍需要在直连 SATA SSD/HDD 上完成硬件矩阵验证。USB bridge、RAID、Storage Spaces 和虚拟磁盘可能只能展示身份/卷信息，并明确显示 health passthrough 不可用。

详细设计见：

- [docs/storage-detail-page-design.md](docs/storage-detail-page-design.md)
- [docs/storage-detail-implementation-plan.md](docs/storage-detail-implementation-plan.md)

## 构建

```powershell
dotnet build
```

当前目标框架为 `net8.0-windows`，硬件摘要主要通过 Windows WMI 采集，CPU 详情的拓扑和缓存信息会优先使用 Windows topology API。

## 内存跑分

内存跑分由 native C++ worker 提供。开发时先构建 worker：

```powershell
.\src\HwScope.Native.MemoryBench\scripts\build-msvc.ps1
```

输出位置：

```text
src\HwScope.Native.MemoryBench\build\Release\membench.exe
```

GUI 中可以通过顶部工具栏 `内存跑分` 或左侧导航 `性能测试 -> 内存跑分` 打开独立窗口，然后点击 `Start Benchmark`。

内存跑分窗口的硬件标题信息来自共享预加载快照，不再依赖先打开或刷新首页概览。

当前跑分仍在持续演进，但已经具备可解释的多线程 Memory 行：

- 已实现 Memory、L1 Cache、L2 Cache、L3 Cache 的 Read / Write / Copy / Latency。
- Read / Write / Copy 默认使用 Windows topology 选择每个物理核心一个 worker，并通过 `SetThreadGroupAffinity` 固定 worker。
- Memory Read / Write / Copy 默认使用 Windows topology 选择每个物理核心一个 worker，并通过 `SetThreadGroupAffinity` 固定 worker。
- L1 / L2 / L3 Cache 行使用 topology-derived working set，并在 preferred physical core 上单线程执行。
- Latency 仍保持单线程语义基线。
- 结果包含 worker/protocol 版本、timer、options、row-level raw samples、aggregate、placement、environment 和 quality flags。
- Copy 主结果按 payload throughput 展示，诊断信息同时记录 estimated traffic throughput。
- native worker 仍需要先通过 CMake 构建；随后 `HwScope.App` / `HwScope.Cli` 构建会把已有的 `membench.exe` 复制到输出目录的 `native\` 子目录。
- runner 带默认超时、取消时会终止 worker 进程树，并把失败诊断写入 `%TEMP%\HwScope-memory-benchmark.log`。
- 后续会继续补 SIMD / non-temporal kernel、NUMA interleaved/per-node 模式、结果历史和导出。

详细设计见 [docs/memory-benchmark-design.md](docs/memory-benchmark-design.md)。L1 / L2 / L3 Cache 行开发方案见 [docs/memory-cache-benchmark-implementation-plan.md](docs/memory-cache-benchmark-implementation-plan.md)。

## 存储跑分

存储跑分由独立 native C++ worker 提供。开发时先构建：

```powershell
.\src\HwScope.Native.StorageBench\scripts\build-msvc.ps1
```

输出位置：

```text
src\HwScope.Native.StorageBench\build\Release\storagebench.exe
```

GUI 可以通过顶部工具栏、`工具 -> 存储跑分` 或左侧导航 `性能测试 -> 存储跑分` 打开独立窗口。当前实现包括：

- `SEQ1M Q8T1`、`SEQ1M Q1T1`、`RND4K Q32T1`、`RND4K Q1T1` 四行 Read / Write / Mix。
- `1/3/5 runs`、`64 MiB/256 MiB/1 GiB/4 GiB`、目标卷、设备/系统缓存模式和测试列选择。
- 主表同时显示 MB/s、IOPS 和 p95 latency；报告保留每轮 sample、p50/p95/p99、aggregate 和 CV。
- 默认设备模式使用 file-backed `FILE_FLAG_NO_BUFFERING`；不会打开或写入 `PhysicalDrive`。
- 测试文件使用随机 session GUID 和 `CREATE_NEW`；写入使用 64 MiB 对齐确定性随机数据池，初始化、warmup、Write/Mix 全部计入最大写入预算。
- `全部开始` 固定按全部 Read、全部 Write、全部 Mix 的顺序执行；Core plan、worker 事件和 UI 总体进度使用同一顺序。
- Core 在创建目录前后复核 volume GUID、volume serial、单一 physical extent 和目录祖先 reparse 属性；worker 创建文件后再从 handle 复核 expected volume GUID。
- Core 对最终结果逐行、逐 operation、逐 sample 校验定义、数量、数值、读写字节、cache mode 和 cleanup；取消先协作停止，再在超时后终止 process tree。
- worker 和父进程双层清理；本地 manifest 记录 volume serial 和 128-bit file ID，下次打开窗口时只允许清理身份仍然一致的孤儿文件，同路径替换文件会被拒绝。
- 打开窗口和切换目标卷只读取已有健康缓存，不主动查询 SMART/Health 或唤醒休眠 HDD；主动温度刷新只发生在用户明确启动跑分之后。
- CLI 支持 `--quick`、`--size-mib`、`--runs`、`--workload` 和 JSON 输出；任何存储跑分都必须显式传入 `--drive`，不会默认选择系统卷。

默认标准计划是 `5 runs / 1 GiB / Read+Write / 0 warmup`，四行合计最大写入 21 GiB。开始前必须确认窗口显示的目标卷和最大写入量；`仅读取` 仍需要一次文件初始化写入。

截至 2026-07-15，Core 共 56 项测试通过，其中 23 项覆盖存储跑分 planner、preflight、worker result contract 和 session cleanup。64 MiB 单行 Q1/Q8 完成、Read -> Write -> Mix 事件顺序、CLI 缺少显式目标时拒绝运行以及运行中取消均已在本机 NVMe 系统卷验证；验证后 manifest 和临时测试文件数量均为 0。当前结果不应直接等同于 CrystalDiskMark；durable/write-through 模式、后台 I/O 检测、BitLocker/电源质量标志和 USB/SATA/HDD 广覆盖仍待补充。

详细需求、安全边界和 UI 设计见 [docs/storage-benchmark-design.md](docs/storage-benchmark-design.md)。

## 主题和配置

GUI 使用 WPF-UI，并在其上叠加 HwScope 自己的 JSON 主题 token。

用户设置默认写入：

```text
%LOCALAPPDATA%\HwScope\settings.json
```

内置主题定义：

```text
src\HwScope.App\Themes\Json\light.json
src\HwScope.App\Themes\Json\dark.json
```

当前支持：

- 主题模式：跟随系统、浅色、深色。
- 背景：Mica 或普通背景。
- 状态栏显示开关。
- JSON 主题校验和 fallback 提示。

详细说明见 [docs/theme-system.md](docs/theme-system.md)。

## 开发建议

推荐使用 Visual Studio 2022 开发 WPF/XAML 和 native C++ worker。VS Code 适合查看代码、编辑文档和运行 CLI，但 WPF 设计、调试、C++ 工程体验不如 VS2022。

如果 Visual Studio 提示“无法直接启动带有‘类库输出类型’的项目”，请在解决方案资源管理器中右键 `HwScope.App`，选择“设为启动项目”。

更多模块边界见 [docs/project-architecture.md](docs/project-architecture.md)。

## 已知限制

- 当前只支持 Windows。
- 首次启动会等待硬件预加载完成或失败后用户选择继续；WMI 或 Windows topology API 响应慢时，进入主窗口时间会变长。
- 硬件摘要依赖 WMI，部分设备字段可能显示为“未识别”。
- CPU 详情页的拓扑/缓存来自 Windows OS topology，不等于完整 CPUID；feature flags 仍等待 native CPUID worker 完整接入。
- CPU topology Visual Map 当前使用 nested domain layout，tree/radial 布局和 PNG/JSON 导出仍在后续阶段。
- CPU code name、工艺、TDP 和部分指令集仍可能来自本地型号映射，页面会标注来源。
- 内存 / SPD 详情页当前仅使用 WMI/SMBIOS；SPD 读取与解析代码已移除，页面固定显示 `SPD 读取暂未实现`。raw SPD、运行态时序和 DIMM/PMIC telemetry 等驱动相关能力暂时搁置。
- 存储详情页的 NVMe 标准 Health 路径已验证；ATA SMART 仍需更多真实 SATA 设备验证。USB/RAID bridge 是否支持协议透传取决于控制器和驱动，不支持时不会被误报为磁盘故障。
- 内存跑分结果目前不应直接对标 AIDA64，kernel、copy accounting、NUMA 和 cache row 仍在演进。
- 存储跑分当前仅支持本地、单 physical extent 的文件系统卷；Storage Spaces/跨盘卷、network share、RAM disk 和 raw disk 被拒绝。结果不应直接对标 CrystalDiskMark。
- native worker 不会由 `dotnet build` 自动编译；需要先运行对应 native 构建脚本生成 Release 产物。

## License

MIT
