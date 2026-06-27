# HwScope

HwScope 是一个 Windows 本地硬件工具箱项目，目标是在一个程序里逐步整合硬件摘要、CPU-Z 类详情、传感器、跑分、压力测试和稳定性查询。

当前版本已经具备：

- WPF 图形界面，基于 WPF-UI / Fluent 风格。
- 首页硬件配置摘要，支持卡片视图和列表视图。
- CPU 详情页，展示身份、规格、频率、拓扑、缓存、核心映射、指令集和平台上下文。
- Windows `GetLogicalProcessorInformationEx` 拓扑采集，提供真实 package/core/thread、CPU group、NUMA、缓存共享和 core-to-logical-processor mapping。
- CLI 硬件摘要输出，支持文本、JSON 和复制到剪贴板。
- 独立内存跑分窗口，界面参考 AIDA64 Cache & Memory Benchmark。
- C++ native 内存跑分 worker，当前测量 Memory Read / Write / Copy / Latency。
- JSON 驱动的主题配置，支持跟随系统、浅色、深色和 Mica 开关。

## 项目结构

```text
HwScope.sln
src/
  HwScope.App/
    WPF GUI 入口，主窗口、硬件摘要页、CPU 详情页、主题系统、内存跑分窗口

  HwScope.Cli/
    命令行入口，复用 HwScope.Core 的硬件采集和跑分能力

  HwScope.Core/
    硬件采集、CPU 详情模型、Windows topology API、格式化、benchmark runner 抽象和 native worker 调用

  HwScope.Native.MemoryBench/
    C++ 内存跑分 worker，输出 CSV 给 HwScope.Core 解析

docs/
  project-architecture.md
  cpu-detail-page-design.md
  cpu-detail-implementation-plan.md
  cpu-stage2-topology-plan.md
  theme-system.md
  memory-benchmark-design.md
```

## 运行

GUI：

```powershell
dotnet run --project .\src\HwScope.App\HwScope.App.csproj
```

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

## CPU 详情页

GUI 中可以通过左侧导航 `硬件 -> 中央处理器 (CPU)` 打开 CPU 详情页。

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

后续重点：

- native CPUID worker，用于 raw family/model/stepping 和真实 feature flags。
- L3 / CCD / V-Cache 洞察，作为启发式信息明确标注。
- 实时频率、传感器、电压、温度和功耗。

详细设计见：

- [docs/cpu-detail-page-design.md](docs/cpu-detail-page-design.md)
- [docs/cpu-detail-implementation-plan.md](docs/cpu-detail-implementation-plan.md)
- [docs/cpu-stage2-topology-plan.md](docs/cpu-stage2-topology-plan.md)

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

GUI 中可以通过顶部工具栏 `跑分` 或左侧导航 `性能测试 -> 内存跑分` 打开独立窗口，然后点击 `Start Benchmark`。

当前跑分仍是第一版原型：

- 已实现 Memory Read / Write / Copy / Latency。
- L1 / L2 / L3 Cache 行暂时是 UI 占位。
- native worker 仍需要手动构建或放在 runner 能找到的位置。
- 后续会补多线程、SIMD kernel、cache row、结果稳定性标记和导出。

详细设计见 [docs/memory-benchmark-design.md](docs/memory-benchmark-design.md)。

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
- 硬件摘要依赖 WMI，部分设备字段可能显示为“未识别”。
- CPU 详情页的拓扑/缓存来自 Windows OS topology，不等于完整 CPUID；feature flags 仍等待 native CPUID worker 完整接入。
- CPU code name、工艺、TDP 和部分指令集仍可能来自本地型号映射，页面会标注来源。
- 内存跑分结果目前不应直接对标 AIDA64，算法和线程策略仍在演进。
- native worker 的打包复制还未完全自动化。

## License

MIT
