# HwScope

HwScope 是一个 Windows C# 硬件工具箱项目。当前阶段先移植了硬件配置摘要 CLI，后续可以在同一套 Core 能力上继续扩展 CPU-Z 类详情、跑分、压力测试、稳定性查询和 GUI。

## 项目结构

```text
HwScope.sln
src/
  HwScope.App/    # WPF 图形界面入口
  HwScope.Core/   # 硬件采集、格式化、后续跑分/稳定性核心能力
  HwScope.Cli/    # 命令行入口
  HwScope.Native.MemoryBench/ # C++ 内存跑分 worker
```

## 运行

GUI：

```powershell
dotnet run --project .\src\HwScope.App
```

CLI：

```powershell
dotnet run --project .\src\HwScope.Cli
```

```powershell
dotnet run --project .\src\HwScope.Cli -- --json
```

```powershell
dotnet run --project .\src\HwScope.Cli -- --copy
```

## 构建

```powershell
dotnet build
```

当前目标框架为 `net8.0-windows`，硬件摘要通过 Windows WMI 采集。

## 内存跑分

内存跑分由 C++ native worker 提供。开发时可先构建：

```powershell
.\src\HwScope.Native.MemoryBench\scripts\build-msvc.ps1
```

GUI 中进入“性能测试 → 内存跑分”，点击 `Start Benchmark` 即可运行。

## Visual Studio

如果 Visual Studio 提示“无法直接启动带有‘类库输出类型’的项目”，请在解决方案资源管理器中右键 `HwScope.App`，选择“设为启动项目”。

## License

MIT
