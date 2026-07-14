# Hardware Preload Design

## Background

Classic hardware tools such as CPU-Z, AIDA64, and HWiNFO perform an upfront load pass before the main experience becomes fully interactive. That pass reduces later page and dialog latency, avoids repeated low-level probes, and creates a shared baseline inventory that downstream tools can trust.

Before this work, HwScope read hardware data lazily in each surface:

- `HardwareSummaryPage` creates `HardwareCollector` and synchronously reads WMI on first load.
- `CpuDetailPage` creates `CpuDetailCollector` and reads WMI plus Windows processor topology on first load.
- `MemoryBenchmarkWindow` receives a summary report from the main window, while the benchmark runner and placement planner still read CPU/topology data on demand.

This design introduces an application-level preload path that builds a reusable hardware inventory snapshot near startup. The current implementation now shows a lightweight startup preload window before the main window, then shares the resulting snapshot with summary, CPU detail, topology inspect, and memory benchmark entry points.

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
- This does not make the preload window a marketing splash screen; it is a functional loading and recovery surface.
- This does not cache `ManagementObject` instances long term.

## Previous Read Paths

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

### Memory / SPD Detail Page

`src/HwScope.App/Pages/MemoryDetailPage.xaml.cs`

- First `Loaded` event calls `App.HardwarePreload.EnsureLoadedAsync()`.
- The page builds `MemoryDetailReport` from the shared snapshot.
- Refresh calls global `App.HardwarePreload.RefreshAsync()`.
- Current implementation uses WMI/SMBIOS-backed `Win32_PhysicalMemory` fields and shows explicit placeholders for raw SPD profiles and runtime controller timings.

### Memory Benchmark

Memory benchmark UI receives a `HardwareReport?` for header display. Core benchmark code still performs its own environment and topology reads:

- `MemoryBenchmarkProcessRunner` reads CPU name/core counts for environment details.
- `MemoryBenchmarkPlacementPlanner` reads logical processor topology for placement and cache row planning.

## Current Implemented Flow

- `App.OnStartup` creates `HardwarePreloadService` and shows `HardwarePreloadWindow`.
- `HardwarePreloadWindow` starts `App.HardwarePreload.RefreshAsync()` after `Loaded`, displays step progress, and attaches to `ThemeService` after the window handle exists.
- On preload success, the window creates `MainWindow`, makes it the application main window, shows it, and closes itself.
- On preload failure, the window shows retry and continue actions. Continue opens the app with no current snapshot, allowing later page refreshes to retry.
- Closing the preload window cancels the startup UI flow so a late background completion cannot reopen the main window.
- `HardwareSummaryPage`, `CpuDetailPage`, `MemoryDetailPage`, and memory benchmark header creation consume `App.HardwarePreload.EnsureLoadedAsync()` or `RefreshAsync()` instead of triggering independent WMI scans.

The underlying WMI calls are still blocking. Cancellation can stop waiting callers and the startup UI flow, but a WMI query already running on the worker thread may continue until the provider returns.

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
  - Manufacturer
  - PartNumber
  - SerialNumber
  - BankLabel
  - DeviceLocator
  - FormFactor
  - DataWidth
  - TotalWidth
  - ConfiguredVoltage
  - MinVoltage
  - MaxVoltage
  - MemoryTypeDetail
  - InterleavePosition
  - Tag
- Video controllers:
  - Name
  - AdapterRAM
  - PNPDeviceID
- Monitors:
  - `WmiMonitorID` UserFriendlyName, ManufacturerName, ProductCodeID
  - `Win32_DesktopMonitor` fallback names
- Disk drives:
  - Index
  - DeviceID
  - PNPDeviceID
  - Model
  - FirmwareRevision
  - SerialNumber
  - Size
  - MediaType
  - InterfaceType
  - BytesPerSector
  - Partitions
  - SCSI bus/port/target/LUN
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
- Storage SMART/NVMe Health, temperature, lifetime and error counters.
- Any device status where stale values would confuse the user.

Dynamic values may be captured as `InitialSnapshot` fields, but pages must label or refresh them appropriately.

## Architecture

### Core Snapshot Models

The pure managed inventory model lives under `src/HwScope.Core/Hardware/Inventory`.

Key records:

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
- `HardwareInventoryCollectionProgress`
- `CpuTopologyAnalysis`, reused for topology data

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

`HardwareInventoryCollector` owns the unified baseline collection pass.

Responsibilities:

- Execute all baseline WMI queries once.
- Execute Windows logical processor topology collection once.
- Convert raw WMI results to pure snapshot records.
- Continue collecting other groups if one group fails.
- Record per-step elapsed time and error/fallback status.

Current public API:

```csharp
public sealed class HardwareInventoryCollector
{
    public HardwareInventorySnapshot Collect(IProgress<HardwareInventoryCollectionProgress>? progress = null);
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
- `cpu-performance`
- `cpu-topology`

Each step is isolated so recoverable WMI/COM/provider exceptions do not abort the whole preload pass. Failed steps record both a short message and full exception text in diagnostics.

### Report Builders

Current collectors now also act as builders over the inventory snapshot.

- Keep existing `HardwareCollector.CollectSummary()` for compatibility.
- Use `HardwareCollector.CreateSummary(HardwareInventorySnapshot snapshot)` for app pages and window headers.
- Keep existing `CpuDetailCollector.Collect()` for compatibility.
- Use `CpuDetailCollector.CreateReport(HardwareInventorySnapshot snapshot)` for the CPU page.

After pages move to preload service, direct no-arg collection remains useful for CLI/tests/fallback.

Longer-term, consider renaming:

- `HardwareSummaryBuilder`
- `CpuDetailReportBuilder`

That can wait until after behavior is stable.

### App Preload Service

`HardwarePreloadService` lives in the app project.

Responsibilities:

- Own the current `HardwareInventorySnapshot`.
- Start preload near application startup.
- Serialize concurrent preload/refresh requests.
- Expose state and diagnostics for UI.
- Raise events when new inventory is available.

Current state:

```csharp
public enum HardwarePreloadState
{
    NotStarted,
    Loading,
    Ready,
    Failed
}
```

Current API:

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

Current startup flow:

1. `App.OnStartup` creates `HardwarePreloadService`.
2. `App.OnStartup` shows `HardwarePreloadWindow` instead of using `StartupUri`.
3. The preload window displays product version, current step, progress count, and failure details.
4. The preload window calls `RefreshAsync()` so startup establishes a fresh baseline snapshot.
5. On `Ready`, it opens `MainWindow`.
6. On recoverable failure, it offers retry or continue.
7. `MainWindow` subscribes to preload progress for footer messages and to inventory changes for its cached hardware report.

## UI Integration

### Hardware Summary Page

The summary page consumes the preload service.

- On first load, call `await App.HardwarePreload.EnsureLoadedAsync()`.
- Convert snapshot to `HardwareReport`.
- Render summary lists from that report.
- Subscribe to `InventoryChanged` so manual global refresh updates the page.
- Refresh button calls `App.HardwarePreload.RefreshAsync()`.

The page should no longer perform synchronous WMI collection on the UI thread.

### CPU Detail Page

The CPU detail page builds its report from the preload snapshot.

- On first load, call `await App.HardwarePreload.EnsureLoadedAsync()`.
- Build `CpuDetailReport` from the snapshot.
- Render normally.
- Refresh button calls global `RefreshAsync()`, then rebuilds the CPU report.
- Existing version guard remains useful to ignore stale UI updates.

CPU topology inspect continues to use `CpuDetailReport.TopologyInspect`, but now the report originates from the preloaded topology analysis.

### Memory / SPD Detail Page

The memory / SPD detail page builds its report from the preload snapshot.

- On first load, call `await App.HardwarePreload.EnsureLoadedAsync()`.
- Build `MemoryDetailReport` from the snapshot.
- Render total capacity, memory type, configured speed, module tiles and selected module details.
- Refresh button calls global `RefreshAsync()`, then rebuilds the report.
- Raw SPD profiles and runtime timings remain provider-backed future data, shown as explicit placeholders rather than inferred values.

### Storage Detail Page

- The preload snapshot supplies the physical disk selector and baseline identity immediately.
- `StorageDetailService` reads partitions, volumes, Windows storage properties and protocol health on demand for the selected device.
- NVMe/ATA health data is dynamic and is not treated as long-lived preload truth.
- Storage page refresh is device-local; it does not rerun CPU, memory, monitor and network inventory just to refresh temperature.
- An inventory refresh still updates the storage device list and removes stale device caches.

### Main Window

Main window no longer uses the summary page as a data source for memory benchmark headers.

- Ensure preload is ready.
- Build/obtain `HardwareReport` from the preload snapshot.
- Pass that report into `MemoryBenchmarkWindow`.

This removes a hidden dependency between the memory benchmark entry and the summary page.

### Memory Benchmark

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
- Full exception text for failed steps.
- Data source notes.

Diagnostics are currently exposed through:

- Status bar short message.
- Preload window failure text.
- `HardwareInventorySnapshot.Diagnostics` for later diagnostics UI.

Future surfaces:

- Dedicated diagnostics window.
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
- Dispatch preload progress and inventory events back to the WPF Dispatcher.
- Do not depend on `Progress<T>` synchronization-context capture for correctness; progress reporting must remain safe when the first caller is not on the UI thread.

## Implementation Plan

### Phase 1: Inventory Snapshot Foundation

- Status: implemented.
- Snapshot records and `HardwareInventoryCollector` are in `HwScope.Core.Hardware.Inventory`.
- Duplicated WMI/topology reads were moved into the collector.
- Raw WMI objects are converted into plain records.
- Diagnostics are recorded per collection step.

### Phase 2: Report Builders

- Status: implemented.
- `HardwareCollector.CreateSummary(HardwareInventorySnapshot snapshot)` builds summary reports from preload snapshots.
- `CpuDetailCollector.CreateReport(HardwareInventorySnapshot snapshot)` builds CPU detail reports from preload snapshots.
- Existing no-arg collection methods remain useful for CLI/tests/fallback.

### Phase 3: App Preload Service

- Status: implemented, with startup window behavior.
- `HardwarePreloadService` is created in `App.OnStartup`.
- Repeated `EnsureLoadedAsync()` calls share the same running task.
- `RefreshAsync()` updates the global snapshot and publishes `InventoryChanged`.
- Progress includes state, message, step name, completed step count, total step count, and item count.

### Phase 4: Page Migration

- Status: implemented.
- `HardwareSummaryPage`, `CpuDetailPage`, and `MemoryDetailPage` consume the preload service.
- Refresh buttons call global refresh.
- Pages subscribe to inventory changes so related views can update from the same snapshot.

### Phase 5: Benchmark Integration

- Status: partially implemented.
- `MainWindow.ShowMemoryBenchmarkAsync()` gets header data from preload service instead of forcing summary refresh.
- Benchmark environment, placement, power, and run-time diagnostics still remain benchmark-time reads.

### Phase 6: Preload UI Polish

- Status: implemented for startup.
- `HardwarePreloadWindow` displays product version, step progress, retry, and continue.
- It uses existing theme resources and attaches to `ThemeService` after `Loaded`.
- Future polish can add a richer diagnostics link/window.

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

- The startup window shows concrete step progress instead of leaving the app blank.
- Failures expose retry and continue actions.
- The window has a minimum visible duration to avoid flicker, but this intentionally adds a small delay when collection is very fast.

### Partial WMI Failure

Some WMI namespaces may fail due to permissions, broken providers, or device quirks.

Mitigation:

- Isolate collection steps.
- Store diagnostics.
- Render partial data.

### Preload Window Lifecycle

The startup window is responsible for opening `MainWindow`. If the user closes it while collection is still in flight, a late completion must not reopen the app.

Mitigation:

- Closing the preload window cancels the startup UI flow.
- Event handlers are detached on close.
- The code checks cancellation before opening `MainWindow`.

### Progress Ordering

Step progress can arrive close to final Ready/Failed state.

Mitigation:

- Progress reporting uses a deterministic inline progress adapter.
- Each load pass has a generation id.
- Older progress is ignored after a newer load starts or after the current pass reaches Ready/Failed.

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
- Memory / SPD detail page renders from the same inventory timestamp after preload.
- Refresh action updates the shared inventory and re-renders consumers.
- Memory benchmark window header no longer depends on forcing summary page refresh.
- Preload diagnostics show step-level success/failure and elapsed time.
- Preload window shows current step progress and exposes retry/continue on failure.
- Existing CPU topology inspect and memory benchmark behavior remains functionally intact.
