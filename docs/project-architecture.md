# HwScope Project Architecture

本文档描述当前 HwScope 的模块边界和开发约定。它记录的是现有实现状态，不代表最终完整硬件工具箱能力已经完成。

## Goals

HwScope 的长期目标是做一个 Windows 本地硬件工具箱，把硬件检测、CPU-Z 类详情、传感器、跑分、压力测试和稳定性查询放在一个程序里。

当前阶段重点是：

- 稳定硬件摘要采集。
- 建立启动期共享硬件 inventory，减少概览页、CPU 页和独立工具窗口的重复读取。
- 建立 CPU-Z/AIDA64 CPUID 风格的 CPU 详情页。
- 建立 GUI shell 和页面拆分。
- 接入相互隔离的 native 内存跑分和文件级存储跑分 worker。
- 建立可扩展的主题配置系统。

## Solution Layout

```text
src/HwScope.App
  WPF GUI app

src/HwScope.Cli
  CLI app

src/HwScope.Core
  shared C# domain and orchestration code

src/HwScope.Native.MemoryBench
  native C++ memory benchmark worker

src/HwScope.Native.StorageBench
  native C++ file-backed storage benchmark worker

src/HwScope.Core.Tests
  Core parser、native buffer boundary、health evaluation、soft timeout、field-source merge、bus formatting 和 formatter tests
```

## HwScope.App

`HwScope.App` 是 WPF 图形界面入口，目标框架为 `net8.0-windows`。

主要组成：

- `MainWindow.xaml`
  主窗口 shell，包含标题栏菜单、HWiNFO 风格快捷工具栏、左侧导航和页面宿主。

- `Windows/HardwarePreloadWindow.xaml`
  启动期硬件预加载窗口。它显示产品版本、当前采集步骤和进度，预加载成功后打开主窗口，失败时允许重试或继续进入应用。

- `Pages/HardwareSummaryPage.xaml`
  首页硬件概览页面，支持卡片视图和列表视图。

- `Pages/CpuDetailPage.xaml`
  CPU 详情页，展示身份、规格、频率、拓扑、缓存、核心映射、指令集和平台上下文。

- `Pages/MemoryDetailPage.xaml`
  内存 / SPD 详情页，展示运行态概览、模块选择、WMI/SMBIOS 模块详情、位宽/电压字段和后续 SPD/时序占位。

- `Pages/StorageDetailPage.xaml`
  存储设备详情页，按物理磁盘展示身份、总线、健康、温度、寿命、协议属性和卷/分区。

- `MemoryBenchmarkWindow.xaml`
  独立内存跑分窗口，参考 AIDA64 的独立 benchmark window。

- `StorageBenchmarkWindow.xaml`
  独立存储跑分窗口，提供目标卷/写入预算参数条、CrystalDiskMark 风格四行结果矩阵、取消、Diagnostics 和导出。

- `Services/HardwarePreloadService.cs`
  App 级共享硬件 inventory 服务，序列化预加载和刷新请求，持有当前 `HardwareInventorySnapshot`，并向页面和窗口发布进度与新快照事件。

- `Services/StorageDetailService.cs`
  按稳定物理设备 ID 缓存动态 storage report，合并同设备并发读取，并用跨设备隔离和 5 秒软超时避免单个阻塞驱动拖住全部磁盘。SMART/temperature 不进入长期 preload snapshot。

- `Configuration/`
  JSON 配置模型和 `%LOCALAPPDATA%\HwScope\settings.json` 读写。

- `Theming/`
  ThemeService、主题模式、Backdrop 模式、JSON 主题加载和运行时资源字典生成。

- `Themes/`
  WPF 控件样式和 JSON 主题 token。

- `Assets/HwScope.ico`
  应用图标资源，用于窗口、任务栏和可执行文件图标。

GUI 依赖 `WPF-UI`，当前版本为 `4.3.0`。

## HwScope.Core

`HwScope.Core` 放共享逻辑，避免 GUI 和 CLI 各自实现硬件检测或跑分流程。

主要命名空间：

- `HwScope.Core.Hardware`
  通过 WMI 采集 CPU、主板、内存、显卡、显示器、硬盘、声卡、网卡摘要。

- `HwScope.Core.Hardware.Inventory`
  共享硬件 inventory 快照模型和统一采集器。采集器把 WMI/Windows API 数据转换成不可变 record，记录每个采集步骤的耗时、状态、数量和异常诊断。

- `HwScope.Core.Hardware.Cpu`
  CPU 详情领域模型、WMI 采集、Windows topology API 聚合、已知型号 fallback、文本报告格式化。

- `HwScope.Core.Hardware.Memory`
  内存 / SPD 详情领域模型、WMI/SMBIOS-backed 报告构建、内存类型/形态格式化和文本报告格式化。当前没有 native SPD worker、SPD JSON provider 或 SPD bytes parser；SPD-only 字段统一显示 `SPD 读取暂未实现`。

- `HwScope.Core.Hardware.Storage`
  存储详情领域模型、物理设备 identity、字段级来源、分区/卷映射、NVMe/ATA parser、健康判定、provider aggregation、soft-timeout policy、轻量 bus probe 和文本报告格式化。

- `HwScope.Core.Windows.Storage`
  只读 Windows storage handle/IOCTL 边界，包含 descriptor/alignment/TRIM query、NVMe protocol-specific health query、legacy ATA SMART attribute/threshold query 和 ATA pass-through RETURN STATUS。所有 buffer offset/length、ATA revision/checksum 和 protocol signature 在进入健康判定前校验。

- `HwScope.Core.Benchmark`
  定义内存跑分结果、选项、runner 抽象和 native worker 进程调用。

- `HwScope.Core.Benchmark.Storage`
  定义存储跑分 target/options/plan/progress/result、volume GUID/serial/extent 与 file ID 查询、安全 preflight、写入预算、session manifest、孤儿清理、结果格式化和 `storagebench.exe` 进程协议。该命名空间只包含 benchmark 所需的窄 Windows identity 查询，不提供 raw physical-drive write API 或任意 IOCTL 输入。

- `HwScope.Core.Windows`
  Windows/WMI 辅助代码，包括 `GetLogicalProcessorInformationEx` 逻辑处理器拓扑读取。

设计原则：

- 硬件采集和结果模型放 Core。
- GUI 只负责展示、交互和窗口生命周期。
- 启动期静态硬件信息通过 `HardwarePreloadService` 共享；页面刷新应刷新全局 inventory，而不是各自重复读取同一批 WMI/Topology 数据。
- CLI 只负责参数解析和输出格式。
- native worker 通过进程隔离，避免底层跑分崩溃带倒 GUI。
- 通用 Windows P/Invoke 和 unsafe buffer parsing 必须留在 `HwScope.Core.Windows`，业务层只消费 typed records。存储跑分的 volume/file identity 查询是受限例外，保留在 `HwScope.Core.Benchmark.Storage`，只暴露 typed identity 且不接受任意设备命令。

## HwScope.Cli

CLI 目前支持：

```powershell
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- --json
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- --copy
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- benchmark memory
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- storage list
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- storage --disk 0
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- storage --disk 0 --json
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- benchmark storage --drive C: --quick
```

CLI 是验证 Core 能力的轻量入口，也适合后续自动化测试和脚本集成。

## HwScope.Native.MemoryBench

native worker 当前通过 CMake 构建 `membench.exe`。首选协议是结构化 JSON / progress JSON，CSV 仅保留为手动兼容格式：

```text
size_mib,read_mib_s,write_mib_s,copy_mib_s,latency_ns
```

`MemoryBenchmarkProcessRunner` 会按以下顺序查找 worker：

```text
AppContext.BaseDirectory\membench.exe
AppContext.BaseDirectory\native\membench.exe
src\HwScope.Native.MemoryBench\build\Release\membench.exe
```

前两个路径用于发布或本地托管项目输出目录；源码树 `build\Release` 路径是 developer fallback，方便手动构建 native worker 后直接运行 App/CLI。

`HwScope.App` 和 `HwScope.Cli` 在构建后会在 native Release 产物存在时复制到各自输出目录的 `native\membench.exe`。`dotnet build` 不会强制编译 C++ worker。

runner 当前有默认超时，取消或超时时会终止 worker 进程树。非零退出码、超时、取消和输出解析失败会把可执行路径、参数、stdout 和 stderr 追加到：

```text
%TEMP%\HwScope-memory-benchmark.log
```

内存跑分 worker 在 Windows 下使用 `QueryPerformanceCounter`，并在结果里记录 timer frequency。每个指标采用 warmup、最小/最大样本数、目标单样本时长、inner loop 自动放大和 CV 收敛判断；GUI 通过 progress JSON 在单项指标完成时实时刷新，最终仍以完整 result JSON 为准。

内存跑分的单线程 placement 复用 Core 里的 Windows CPU topology 读取，默认选择一个 preferred physical core，并让 native worker 通过 `SetThreadGroupAffinity` 执行和回报 requested/actual placement。

## HwScope.Native.StorageBench

`storagebench.exe` 是独立的 Windows file-backed worker。Core 只传入经过 planner/preflight 生成的 GUID 临时文件路径和 expected volume GUID；worker 首先使用 `CREATE_NEW` 创建文件，从已打开 handle 复核 volume GUID 并报告 volume serial/128-bit file ID，随后只以 `OPEN_EXISTING` 重开同一个路径执行测试。协议不存在 `PhysicalDrive` 或任意 raw path 模式。

当前 worker 使用 overlapped I/O + IO completion port 实现：

```text
SEQ1M Q8T1
SEQ1M Q1T1
RND4K Q32T1
RND4K Q1T1
```

每行支持 Read、Write 和可选 R70/W30 Mix，并固定按全部 Read、全部 Write、全部 Mix 的顺序执行。设备模式设置 `FILE_FLAG_NO_BUFFERING`，系统缓存模式保留 Windows file cache；两种模式都不等同于 write-through。写入数据来自计时前生成的 64 MiB 对齐确定性随机池；每轮采用固定 bytes plan，因此文件初始化、warmup、write 和 mix write 可以在开始前进入 checked write budget。

Core 与 worker 使用 protocol version 1 的 newline-delimited JSON：`phase`、`file_created`、workload 和 sample 事件更新状态，其中 `file_created` 把已创建文件的 volume serial/128-bit file ID 原子写回 manifest。成功结果必须同时包含 `file_created`、`result` 和 `completed`；最终 `result` 是唯一权威成绩，并按 plan 严格校验 row 定义、operation、samples、有限非负数值、读写字节、cache mode 和 cleanup。取消通过 stdin control message 请求 worker 调用 `CancelIoEx` 并 drain 全部 completion；超过 2 秒后 Core 终止 process tree。worker、Core parent cleanup 和 `%LOCALAPPDATA%\HwScope\StorageBench\Manifests` 构成三层残留治理，孤儿删除前还会重新匹配 volume serial 和 file ID。

`HwScope.App` / `HwScope.Cli` 构建会在 Release worker 已存在时复制 `storagebench.exe` 到输出目录的 `native\`，但 `dotnet build` 不负责调用 CMake。

## GUI Startup And Preload

当前 GUI 启动顺序：

1. `App.OnStartup` 检查管理员身份；未提权时请求 UAC 提权并重启当前可执行文件。开发进程可以用 `HWSCOPE_SKIP_ELEVATION=1` 跳过该步骤，但底层能力仍按实际权限降级。
2. 用户取消或提权失败时，App 显示可关闭的权限提示，然后继续普通权限启动。
3. App 创建 `ThemeService`、`SingleInstanceWindowManager`、`HardwarePreloadService` 和按设备缓存动态健康报告的 `StorageDetailService`。
4. App 显示 `HardwarePreloadWindow`，窗口在 `Loaded` 后接入 `ThemeService`。
5. `HardwarePreloadWindow` 调用 `App.HardwarePreload.RefreshAsync()`，显示步骤进度。
6. 预加载成功后创建并显示 `MainWindow`，再关闭预加载窗口。
7. 预加载失败时，用户可以重试，或继续进入主窗口并在后续页面刷新时重试。

`HardwarePreloadService` 会合并并发加载请求，向 UI 发布 `ProgressChanged` 和 `InventoryChanged`。进度回调不依赖 `SynchronizationContext` 捕获，最终状态和事件发布会回到 WPF Dispatcher。关闭预加载窗口会取消启动 UI 流程，避免后台任务完成后重新打开主窗口；底层 WMI 查询本身仍可能运行到当前阻塞调用返回。

硬件概览页、CPU 详情页和内存 / SPD 详情页在首次加载时调用 `EnsureLoadedAsync()`，刷新按钮调用全局 `RefreshAsync()`。内存跑分窗口的 header 信息也从共享快照构建，不再依赖概览页先刷新。

存储跑分窗口打开和切换目标卷时只调用 `StorageDetailService.TryGetCached()`，不会主动读取 SMART/Health 或唤醒休眠 HDD；用户明确开始跑分后，温度刷新才允许作为 best-effort 操作执行，并且不与 timed I/O 并行。

## Branching

当前仓库按用户约定使用：

- `feat/...`：新功能和较大阶段开发。
- `fix/...`：问题修复。
- `chore/...`：工程维护、文档、构建脚本等。

Fluent UI shell、主题系统、快捷工具栏和应用图标已在 `v0.1.1` 合入 `main`。`feat/...` 分支保留为阶段开发和历史追踪分支。

## Build And Validation

常规验证：

```powershell
dotnet build
```

如果 WPF app 正在运行导致构建锁文件，可以先关闭：

```powershell
Get-Process HwScope.App -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build
```

native worker 构建：

```powershell
.\src\HwScope.Native.MemoryBench\scripts\build-msvc.ps1
.\src\HwScope.Native.StorageBench\scripts\build-msvc.ps1
```

## Diagnostics

GUI 的全局异常、打开内存跑分窗口时的异常和部分硬件预加载诊断会写入临时日志或 inventory diagnostics：

```text
%TEMP%\HwScope-crash.log
```

这个日志主要用于开发期定位 WPF 窗口生命周期、主题 watcher 和 native worker 调用异常。硬件预加载的单步异常会保存在 `HardwareInventoryStepDiagnostic.ExceptionText` 中，供后续诊断入口展示。发布前可以把这些信息整理成统一诊断服务或可配置日志。

## Near-Term Engineering Tasks

- 继续稳定启动预加载窗口、内存/存储跑分窗口打开、主题附着和窗口生命周期。
- 将 native worker 构建纳入更完整的开发/发布流水线。
- 为主题系统补配置迁移和用户自定义主题枚举。
- 为 CPU 详情补 native CPUID worker、真实 feature flags 和 L3/CCD 洞察。
- SPD EEPROM/SPD Hub、运行态时序和 DIMM/PMIC telemetry 保持搁置；重启前先完成受控内核驱动安全模型、平台支持矩阵、签名/分发和故障隔离评审。
- 为硬件摘要补更细的主板、显卡详情页。
- 为 benchmark runner 增加更明确的用户可见错误分类和可配置诊断入口。
