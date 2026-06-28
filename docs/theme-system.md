# Theme System

HwScope GUI 使用 WPF-UI 提供基础 Fluent 控件和系统主题能力，同时使用自己的 JSON token 定义应用内颜色资源。

## Goals

主题系统的目标是：

- 支持跟随系统、浅色、深色。
- 支持 Mica 背景开关。
- 让用户设置持久化到本地 JSON。
- 让应用颜色 token 可从 JSON 读取，后续支持自定义主题。
- 避免主题切换逻辑散落在窗口事件里。

## Files

```text
src/HwScope.App/Configuration/
  AppSettings.cs
  ThemeSettings.cs
  WindowSettings.cs
  JsonSettingsStore.cs

src/HwScope.App/Theming/
  ThemeMode.cs
  BackdropMode.cs
  ThemeDefinition.cs
  ThemeDefinitionStore.cs
  ThemeLoadResult.cs
  ThemeResourceBuilder.cs
  ThemeService.cs

src/HwScope.App/Themes/
  HwScope.Controls.xaml
  Json/light.json
  Json/dark.json
```

旧的 `HwScope.Light.xaml`、`HwScope.Dark.xaml` 和 `HwScope.Colors.xaml` 已经删除。颜色资源现在由 JSON 主题定义在运行时生成。

`HwScope.Controls.xaml` 只保留应用级控件样式，不再承载固定主题颜色。当前主要包含硬件摘要卡片、CPU 详情字段、快捷工具栏按钮等共享样式；这些样式应通过 `DynamicResource` 消费 JSON 主题生成的 brush。

## User Settings

用户设置路径：

```text
%LOCALAPPDATA%\HwScope\settings.json
```

当前结构：

```json
{
  "schemaVersion": 1,
  "theme": {
    "mode": "System",
    "backdrop": "Mica",
    "accent": "Default",
    "density": "Default",
    "customThemeId": null
  },
  "window": {
    "showStatusBar": false
  }
}
```

`mode` 可取：

```text
System
Light
Dark
```

`backdrop` 可取：

```text
Mica
None
```

## Theme Definition

内置主题定义在：

```text
src/HwScope.App\Themes\Json\light.json
src/HwScope.App\Themes\Json\dark.json
```

每个主题包含 `id`、`displayName`、`base` 和 `tokens`：

```json
{
  "id": "light",
  "displayName": "浅色",
  "base": "Light",
  "tokens": {
    "HwScopePanelColor": "#F5F5F5",
    "HwScopeContentColor": "#F5F5F5",
    "HwScopeCardColor": "#FFFFFFFF",
    "HwScopeLineColor": "#E5E5E5",
    "HwScopeTextColor": "#2F2F2F",
    "HwScopeStrongTextColor": "#4B5563",
    "HwScopeMutedTextColor": "#626262",
    "HwScopeIconColor": "#3F4752",
    "HwScopeIconBackplateColor": "#F3F4F6",
    "HwScopeActiveViewColor": "#EDEDED"
  }
}
```

`ThemeResourceBuilder` 会把 `*Color` token 转成同名 `Color` 资源，并额外生成对应的 `*Brush` 资源。

示例：

```text
HwScopePanelColor -> HwScopePanelBrush
HwScopeTextColor  -> HwScopeTextBrush
```

XAML 中应优先使用 `DynamicResource`：

```xml
Background="{DynamicResource HwScopePanelBrush}"
Foreground="{DynamicResource HwScopeTextBrush}"
```

## Runtime Flow

启动流程：

1. `App.OnStartup` 创建 `ThemeService`。
2. `JsonSettingsStore` 读取或创建 `%LOCALAPPDATA%\HwScope\settings.json`。
3. `ThemeService.ApplyCurrentTheme()` 根据设置加载 JSON 主题。
4. `ThemeResourceBuilder` 生成运行时 `ResourceDictionary`。
5. `ApplicationThemeManager.Apply(...)` 应用 WPF-UI 主题和 backdrop。
6. 窗口 `Loaded` 后通过 `ThemeService.Attach(window)` 接入窗口级主题监听。

`ThemeService` 是主题切换唯一入口。窗口菜单只调用：

```csharp
App.ThemeService.SetThemeMode(ThemeMode.Dark);
App.ThemeService.SetBackdrop(BackdropMode.Mica);
App.ThemeService.SetShowStatusBar(true);
```

## Validation And Fallback

`ThemeDefinitionStore` 会校验：

- 主题 JSON 文件是否存在。
- JSON 是否能反序列化。
- `id` 是否存在并与文件匹配。
- 必需 token 是否存在。
- token 颜色值是否有效。

校验失败时会使用内置 fallback 主题，并通过 `ThemeService.StatusChanged` 通知窗口状态栏。

## Window Lifecycle Notes

WPF-UI 的 `SystemThemeWatcher.Watch/UnWatch` 依赖窗口句柄。不要在窗口构造函数里直接调用 watcher。

当前策略：

- `ThemeService.Attach(window)` 可以登记窗口。
- 只有窗口 `Loaded` 且有 presentation source 时才调用 `SystemThemeWatcher.Watch`。
- 窗口关闭时会自动 `Detach`，并安全跳过无句柄窗口的 `UnWatch`。

内存跑分窗口在自身 `Loaded` 后接入 ThemeService，避免构造期访问未加载窗口句柄。

## Future Work

- 增加设置版本迁移。
- 支持用户主题目录，例如 `%LOCALAPPDATA%\HwScope\Themes\*.json`。
- 在设置页中枚举主题、预览 token 和切换密度。
- 把 crash log 入口整理成统一诊断服务。
