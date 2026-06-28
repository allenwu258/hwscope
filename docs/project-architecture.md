# HwScope Project Architecture

本文档描述当前 HwScope 的模块边界和开发约定。它记录的是现有实现状态，不代表最终完整硬件工具箱能力已经完成。

## Goals

HwScope 的长期目标是做一个 Windows 本地硬件工具箱，把硬件检测、CPU-Z 类详情、传感器、跑分、压力测试和稳定性查询放在一个程序里。

当前阶段重点是：

- 稳定硬件摘要采集。
- 建立 CPU-Z/AIDA64 CPUID 风格的 CPU 详情页。
- 建立 GUI shell 和页面拆分。
- 接入 native 内存跑分 worker。
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
```

## HwScope.App

`HwScope.App` 是 WPF 图形界面入口，目标框架为 `net8.0-windows`。

主要组成：

- `MainWindow.xaml`
  主窗口 shell，包含标题栏菜单、HWiNFO 风格快捷工具栏、左侧导航和页面宿主。

- `Pages/HardwareSummaryPage.xaml`
  首页硬件概览页面，支持卡片视图和列表视图。

- `Pages/CpuDetailPage.xaml`
  CPU 详情页，展示身份、规格、频率、拓扑、缓存、核心映射、指令集和平台上下文。

- `MemoryBenchmarkWindow.xaml`
  独立内存跑分窗口，参考 AIDA64 的独立 benchmark window。

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

- `HwScope.Core.Hardware.Cpu`
  CPU 详情领域模型、WMI 采集、Windows topology API 聚合、已知型号 fallback、文本报告格式化。

- `HwScope.Core.Benchmark`
  定义内存跑分结果、选项、runner 抽象和 native worker 进程调用。

- `HwScope.Core.Windows`
  Windows/WMI 辅助代码，包括 `GetLogicalProcessorInformationEx` 逻辑处理器拓扑读取。

设计原则：

- 硬件采集和结果模型放 Core。
- GUI 只负责展示、交互和窗口生命周期。
- CLI 只负责参数解析和输出格式。
- native worker 通过进程隔离，避免底层跑分崩溃带倒 GUI。
- Windows P/Invoke 和 unsafe buffer parsing 必须留在 `HwScope.Core.Windows`，业务层只消费 typed records。

## HwScope.Cli

CLI 目前支持：

```powershell
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- --json
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- --copy
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- benchmark memory
```

CLI 是验证 Core 能力的轻量入口，也适合后续自动化测试和脚本集成。

## HwScope.Native.MemoryBench

native worker 当前通过 CMake 构建 `membench.exe`，并输出 CSV：

```text
size_mib,read_mib_s,write_mib_s,copy_mib_s,latency_ns
```

`MemoryBenchmarkProcessRunner` 会按以下顺序查找 worker：

```text
AppContext.BaseDirectory\membench.exe
AppContext.BaseDirectory\native\membench.exe
src\HwScope.Native.MemoryBench\build\Release\membench.exe
C:\Users\Trivedi\memory-bench-cpp\build\Release\membench.exe
```

外部 prototype 路径只适合开发期，发布前应删除或改为明确的 developer-only fallback。

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
```

## Diagnostics

GUI 的全局异常和打开内存跑分窗口时的异常会写入临时日志：

```text
%TEMP%\HwScope-crash.log
```

这个日志主要用于开发期定位 WPF 窗口生命周期、主题 watcher 和 native worker 调用异常。发布前可以把它整理成统一诊断服务或可配置日志。

## Near-Term Engineering Tasks

- 继续稳定内存跑分窗口打开、主题附着和窗口生命周期。
- 自动复制 `membench.exe` 到 GUI 输出目录。
- 为主题系统补配置迁移和用户自定义主题枚举。
- 为 CPU 详情补 native CPUID worker、真实 feature flags 和 L3/CCD 洞察。
- 为硬件摘要补更细的内存、主板、显卡详情页。
- 为 benchmark runner 增加 timeout、取消和更明确的错误分类。
