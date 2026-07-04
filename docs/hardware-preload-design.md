# Hardware Preload Design

## Background

Classic hardware tools such as CPU-Z, AIDA64, and HWiNFO perform an upfront load pass before the main experience becomes fully interactive. That pass reduces later page and dialog latency, avoids repeated low-level probes, and creates a shared baseline inventory that downstream tools can trust.

HwScope currently reads hardware data lazily in each surface:

- `HardwareSummaryPage` creates `HardwareCollector` and synchronously reads WMI on first load.
- `CpuDetailPage` creates `CpuDetailCollector` and reads WMI plus Windows processor topology on first load.
- `MemoryBenchmarkWindow` receives a summary report from the main window, while the benchmark runner and placement planner still read CPU/topology data on demand.

This design introduces an application-level preload path that builds a reusable hardware inventory snapshot near startup.

## Goals

- Build one shared baseline hardware inventory for summary, CPU details, topology inspect, and benchmark setup.
- Remove duplicate WMI and topology reads from page open paths.
- Keep first page/dialog interactions responsive after preload completes.
- Preserve explicit user refresh behavior.
- Keep dynamic data, benchmark measurements, and volatile telemetry out of the static baseline unless clearly marked as a snapshot.
- Capture preload diagnostics so partial failures are visible and debuggable.

## Non-Goals

- This does not implement live sensor polling.
- This does not make memory benchmark results part of the preload database.
- This does not require a branded splash screen in the first iteration.
- This does not cache `ManagementObject` instances long term.

## Current Read Paths

### Summary Page

`src/HwScope.App/Pages/HardwareSummaryPage.xaml.cs`

- First `Loaded` event calls `RefreshHardwareSummary()`.
- `RefreshHardwareSummary()` calls `HardwareCollector.CollectSummary()`.
- Collection is synchronous on the UI path.
- It reads CPU, baseboard, memory, graphics, display, disk, audio, and network data.

### CPU Detail Page

`src/HwScope.App/Pages/CpuDetailPage.xaml.cs`

- First `Loaded` event calls `RefreshAsync()`.
- `RefreshAsync()` uses `Task.Run(_collector.Collect)`.
- `CpuDetailCollector.Collect()` reads CPU, baseboard, BIOS, memory, graphics, current clock, and Windows logical processor topology.
- The CPU topology inspect window is built from the resulting `CpuDetailReport.TopologyInspect`.

### Memory Benchmark

Memory benchmark UI receives a `HardwareReport?` for header display. Core benchmark code still performs its own environment and topology reads:

- `MemoryBenchmarkProcessRunner` reads CPU name/core counts for environment details.
- `MemoryBenchmarkPlacementPlanner` reads logical processor topology for placement and cache row planning.

## Preload Candidates

### Static or Mostly Static Baseline

These should be captured in the shared inventory snapshot:

- Processor WMI fields:
  - Name
  - Manufacturer
  - Description
  - NumberOfCores
  - NumberOfLogicalProcessors
  - MaxClockSpeed
  - CurrentClockSpeed as initial snapshot only
  - SocketDesignation
  - ProcessorId
  - Architecture
  - Family
  - Revision
  - Stepping
- Baseboard:
  - Manufacturer
  - Product
- BIOS:
  - SMBIOSBIOSVersion
  - Version
  - ReleaseDate
- Physical memory modules:
  - Capacity
  - Speed
  - ConfiguredClockSpeed
  - SMBIOSMemoryType
  - MemoryType
  - Later extension candidates: Manufacturer, PartNumber, SerialNumber, BankLabel, DeviceLocator
- Video controllers:
  - Name
  - AdapterRAM
  - PNPDeviceID
- Monitors:
  - `WmiMonitorID` UserFriendlyName, ManufacturerName, ProductCodeID
  - `Win32_DesktopMonitor` fallback names
- Disk drives:
  - Model
  - Size
  - MediaType
  - InterfaceType
- Sound devices:
  - Name
- Network adapters:
  - Name
  - NetConnectionStatus as initial snapshot only
  - PhysicalAdapter
  - AdapterType
  - Speed
- Windows processor topology:
  - Processor groups
  - Packages
  - NUMA nodes
  - Core mappings
  - Cache instances
  - Topology inspect report
- Local CPU mapping/catalog result:
  - Display name
  - Code name
  - Package
  - Technology
  - TDP
  - Known cache/features fallback data

### Dynamic or On-Demand Data

These should not become long-lived preload truth:

- Live CPU frequency and utilization.
- Battery and AC power state used for benchmark diagnostics.
- Memory benchmark metrics and samples.
- Sensor data once added.
- Any device status where stale values would confuse the user.

Dynamic values may be captured as `InitialSnapshot` fields, but pages must label or refresh them appropriately.

## Architecture

### Core Snapshot Models

Add a pure managed inventory model under `src/HwScope.Core/Hardware/Inventory`.

Suggested records:

- `HardwareInventorySnapshot`
- `HardwareInventoryDiagnostics`
- `HardwareInventoryStepDiagnostic`
- `ProcessorSnapshot`
- `BaseBoardSnapshot`
- `BiosSnapshot`
- `MemoryModuleSnapshot`
- `VideoControllerSnapshot`
- `MonitorSnapshot`
- `DiskDriveSnapshot`
- `AudioDeviceSnapshot`
- `NetworkAdapterSnapshot`
- `CpuTopologySnapshot` or reuse `CpuTopologyAnalysis` where practical

Important rule: do not expose or cache `System.Management.ManagementObject`. WMI and COM objects should be converted to plain records during collection.

Example shape:

```csharp
public sealed record HardwareInventorySnapshot(
    IReadOnlyList<ProcessorSnapshot> Processors,
    BaseBoardSnapshot? BaseBoard,
    BiosSnapshot? Bios,
    IReadOnlyList<MemoryModuleSnapshot> MemoryModules,
    IReadOnlyList<VideoControllerSnapshot> VideoControllers,
    IReadOnlyList<MonitorSnapshot> Monitors,
    IReadOnlyList<DiskDriveSnapshot> DiskDrives,
    IReadOnlyList<AudioDeviceSnapshot> AudioDevices,
    IReadOnlyList<NetworkAdapterSnapshot> NetworkAdapters,
    CpuTopologyAnalysis? CpuTopology,
    HardwareInventoryDiagnostics Diagnostics,
    DateTimeOffset GeneratedAt);
```

### Unified Collector

Add `HardwareInventoryCollector`.

Responsibilities:

- Execute all baseline WMI queries once.
- Execute Windows logical processor topology collection once.
- Convert raw WMI results to pure snapshot records.
- Continue collecting other groups if one group fails.
- Record per-step elapsed time and error/fallback status.

Suggested public API:

```csharp
public sealed class HardwareInventoryCollector
{
    public HardwareInventorySnapshot Collect();
}
```

Collection should be step-based:

- `processors`
- `baseboard`
- `bios`
- `memory`
- `video`
- `monitors`
- `disks`
- `audio`
- `network`
- `cpu-topology`

Each step should be isolated so a `ManagementException`, `UnauthorizedAccessException`, or `COMException` does not abort the whole preload pass.

### Report Builders

Refactor current collectors into builders over the inventory snapshot.

Recommended transition:

- Keep existing `HardwareCollector.CollectSummary()` for compatibility.
- Add `HardwareCollector.CreateSummary(HardwareInventorySnapshot snapshot)`.
- Keep existing `CpuDetailCollector.Collect()` for compatibility.
- Add `CpuDetailCollector.CreateReport(HardwareInventorySnapshot snapshot)`.

After pages move to preload service, direct no-arg collection remains useful for CLI/tests/fallback.

Longer-term, consider renaming:

- `HardwareSummaryBuilder`
- `CpuDetailReportBuilder`

That can wait until after behavior is stable.

### App Preload Service

Add `HardwarePreloadService` in the app project.

Responsibilities:

- Own the current `HardwareInventorySnapshot`.
- Start preload near application startup.
- Serialize concurrent preload/refresh requests.
- Expose state and diagnostics for UI.
- Raise events when new inventory is available.

Suggested state:

```csharp
public enum HardwarePreloadState
{
    NotStarted,
    Loading,
    Ready,
    Failed
}
```

Suggested API:

```csharp
public sealed class HardwarePreloadService
{
    public HardwarePreloadState State { get; }
    public HardwareInventorySnapshot? Current { get; }
    public string? LastStatusMessage { get; }

    public event EventHandler<HardwarePreloadProgress>? ProgressChanged;
    public event EventHandler<HardwareInventorySnapshot>? InventoryChanged;

    public Task<HardwareInventorySnapshot> EnsureLoadedAsync(CancellationToken cancellationToken = default);
    public Task<HardwareInventorySnapshot> RefreshAsync(CancellationToken cancellationToken = default);
}
```

Attach it to `App` similarly to `ThemeService`:

```csharp
public static HardwarePreloadService HardwarePreload { get; private set; } = null!;
```

### Startup Flow

First iteration:

1. `App.OnStartup` creates `HardwarePreloadService`.
2. `MainWindow.Loaded` starts preload in the background.
3. Footer/status bar shows preload progress.
4. Summary page waits for preload and renders immediately when ready.
5. CPU page waits for the same snapshot and builds its report from it.

Second iteration:

1. Add a lightweight preload window or startup overlay.
2. Display product/version, current step, progress bar, and diagnostics hint.
3. Show the main window when preload reaches `Ready` or enters a recoverable `Failed` state.

## UI Integration

### Hardware Summary Page

Replace private `HardwareCollector` ownership with preload service consumption.

New behavior:

- On first load, call `await App.HardwarePreload.EnsureLoadedAsync()`.
- Convert snapshot to `HardwareReport`.
- Render summary lists from that report.
- Subscribe to `InventoryChanged` so manual global refresh updates the page.
- Refresh button calls `App.HardwarePreload.RefreshAsync()`.

The page should no longer perform synchronous WMI collection on the UI thread.

### CPU Detail Page

Replace private `CpuDetailCollector` ownership with snapshot-to-report build.

New behavior:

- On first load, call `await App.HardwarePreload.EnsureLoadedAsync()`.
- Build `CpuDetailReport` from the snapshot.
- Render normally.
- Refresh button calls global `RefreshAsync()`, then rebuilds the CPU report.
- Existing version guard remains useful to ignore stale UI updates.

CPU topology inspect continues to use `CpuDetailReport.TopologyInspect`, but now the report originates from the preloaded topology analysis.

### Main Window

Main window should stop using summary page as a data source for memory benchmark headers.

Current behavior:

- If `_currentReport` is null, `ShowMemoryBenchmark()` forces `_hardwareSummaryPage.RefreshHardwareSummary()`.

Recommended behavior:

- Ensure preload is ready.
- Build/obtain `HardwareReport` from the preload snapshot.
- Pass that report into `MemoryBenchmarkWindow`.

This removes a hidden dependency between the memory benchmark entry and the summary page.

### Memory Benchmark

Short-term:

- Use preloaded `HardwareReport` for window header fields.

Medium-term:

- Allow `MemoryBenchmarkPlacementPlanner` to accept preloaded topology.
- Allow benchmark environment metadata to accept preloaded CPU name/core counts.
- Keep benchmark-time power/battery diagnostics on-demand.

## Refresh Semantics

Refresh should be global, not page-local.

- User clicks summary refresh: refresh inventory snapshot, then summary and CPU pages can re-render.
- User clicks CPU refresh: refresh inventory snapshot, then CPU and summary pages can re-render.
- Existing independent windows should keep their current session state unless explicitly closed/reopened.
- CPU topology inspect should keep old contents if already open, matching the current singleton session behavior.

Potential later option: add a "Reload inspect from latest inventory" action.

## Diagnostics

Preload diagnostics should include:

- Generated timestamp.
- Total elapsed time.
- Per-step elapsed time.
- Step status: success, empty, failed, skipped.
- Exception message for failed steps.
- Data source notes.

Expose diagnostics in one of these surfaces:

- Status bar short message.
- Future diagnostics window.
- CPU detail notes section for relevant CPU/topology fallback messages.

## Error Handling

The preload pass should be best-effort.

- If monitor WMI fails, still load CPU, memory, disk, etc.
- If topology API fails, CPU page falls back to WMI counts and known catalog cache data.
- If all critical CPU data fails, pages show unavailable state and allow retry.
- Exceptions should be stored in diagnostics, not thrown through normal UI rendering unless no usable data exists.

## Threading

- Run collection off the UI thread.
- Publish immutable snapshots back to UI code.
- Avoid sharing mutable collections.
- Avoid exposing WMI objects beyond collection methods.
- Serialize refreshes to prevent competing WMI scans.

## Implementation Plan

### Phase 1: Inventory Snapshot Foundation

- Add snapshot records.
- Add `HardwareInventoryCollector`.
- Move duplicated WMI query logic into the collector.
- Convert raw WMI results into plain records.
- Add diagnostics per collection step.
- Keep existing page behavior unchanged.

### Phase 2: Report Builders

- Add `HardwareCollector.CreateSummary(HardwareInventorySnapshot snapshot)`.
- Add `CpuDetailCollector.CreateReport(HardwareInventorySnapshot snapshot)`.
- Preserve existing no-arg collection methods through a compatibility path.
- Add focused tests or smoke checks for summary and CPU report generation from a synthetic snapshot.

### Phase 3: App Preload Service

- Add `HardwarePreloadService`.
- Create it in `App.OnStartup`.
- Start preload from `MainWindow.Loaded`.
- Wire status/progress to footer.
- Ensure repeated `EnsureLoadedAsync()` calls share the same task.

### Phase 4: Page Migration

- Migrate `HardwareSummaryPage` to consume preload service.
- Migrate `CpuDetailPage` to consume preload service.
- Change refresh buttons to call global refresh.
- Remove page-owned collector instances.

### Phase 5: Benchmark Integration

- Change `MainWindow.ShowMemoryBenchmark()` to get header data from preload service instead of forcing summary refresh.
- Pass preloaded CPU/topology data into benchmark environment/placement where useful.
- Keep benchmark-specific diagnostics fresh at run time.

### Phase 6: Preload UI Polish

- Add optional startup preload window or overlay.
- Display step progress and version.
- Add diagnostics access.
- Tune visual style to match HwScope rather than copying CPU-Z/AIDA64 directly.

## Risks and Mitigations

### Stale Data

Hardware inventory is mostly static, but some fields are not.

Mitigation:

- Label initial dynamic values as snapshot fields.
- Refresh globally on user request.
- Keep benchmark-time and sensor-time values on demand.

### Slower Startup

Moving work earlier can make startup feel slower.

Mitigation:

- First iteration uses background preload.
- UI remains usable with loading states.
- Splash/overlay can be introduced only after the pipeline is stable.

### Partial WMI Failure

Some WMI namespaces may fail due to permissions, broken providers, or device quirks.

Mitigation:

- Isolate collection steps.
- Store diagnostics.
- Render partial data.

### Cross-Thread WMI Object Lifetime

`ManagementObject` instances should not be cached.

Mitigation:

- Convert to pure records immediately.
- Snapshot records are immutable and UI-safe.

### Divergent Report Semantics

Moving from direct WMI reads to snapshot builders may accidentally change formatting.

Mitigation:

- Preserve current formatter behavior during Phase 2.
- Use synthetic snapshots that mirror current WMI values.
- Compare generated `HardwareReport` and `CpuDetailReport` against existing collector output during development.

## Acceptance Criteria

- App creates a shared hardware inventory preload service.
- Summary page does not synchronously read WMI on first load.
- CPU page does not independently read the same WMI groups on first load.
- Summary and CPU pages render from the same inventory timestamp after preload.
- Refresh action updates the shared inventory and re-renders consumers.
- Memory benchmark window header no longer depends on forcing summary page refresh.
- Preload diagnostics show step-level success/failure and elapsed time.
- Existing CPU topology inspect and memory benchmark behavior remains functionally intact.

