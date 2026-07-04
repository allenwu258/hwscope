# L1/L2/L3 Cache Benchmark Implementation Plan

本文档记录在 `feat/memory-benchmark` 分支上实现 L1/L2/L3 Cache 跑分行的开发方案及第一版落地状态。它基于当前 `docs/memory-benchmark-design.md`、现有 C# runner、WPF 窗口、native C++ worker 和 Windows topology 采集实现。

Status: baseline implemented. Protocol 6, row-level results, Core topology row planning, native cache row execution, CLI table output, and WPF row rendering are in place.

## Goal

当前内存跑分窗口已经有 AIDA64 风格表格：

```text
          Read    Write    Copy    Latency
Memory
L1 Cache
L2 Cache
L3 Cache
```

Memory、L1/L2/L3 行现在都有真实结果。此阶段目标是让 L1/L2/L3 行输出真实、可解释、可诊断的 cache-local benchmark 结果。

第一版优先级：

1. 正确区分 Memory、L1、L2、L3 working set。
2. 用现有 Windows topology 检测 cache size、line size 和 sharing mask。
3. 保持 benchmark 结果可解释，输出 raw samples、aggregate、placement、working set metadata 和 quality hints。
4. 保持现有 Memory 多线程带宽行为不退化。
5. 保留旧字段兼容，降低 CLI、GUI 和 diagnostics 的迁移风险。

非目标：

- 不在本阶段实现 SIMD/AVX/non-temporal kernels。
- 不在本阶段追求与 AIDA64 绝对数值完全对齐。
- 不在本阶段实现 NUMA interleaved/per-node memory bandwidth。
- 不在本阶段引入 native CPUID worker；cache topology 先复用 `GetLogicalProcessorInformationEx`。

## Current Baseline

相关文件：

- `src/HwScope.Native.MemoryBench/src/main.cpp`
- `src/HwScope.Core/Benchmark/MemoryBenchmarkProcessRunner.cs`
- `src/HwScope.Core/Benchmark/MemoryBenchmarkPlacementPlanner.cs`
- `src/HwScope.Core/Benchmark/MemoryBenchmarkResult.cs`
- `src/HwScope.Core/Windows/LogicalProcessorInformation.cs`
- `src/HwScope.App/MemoryBenchmarkWindow.xaml`
- `src/HwScope.App/MemoryBenchmarkWindow.xaml.cs`
- `src/HwScope.Cli/Program.cs`

当前行为：

- native worker protocol version 为 `6`。
- Memory Read/Write/Copy 默认使用 Core 规划的 physical-core worker 集合。
- Memory Latency 保持单线程 pointer chasing。
- `MemoryBenchmarkResult` 顶层字段 `ReadMiBS`、`WriteMiBS`、`CopyMiBS`、`LatencyNs` 表示 Memory 行。
- Progress JSON 包含 row 和 metric。
- GUI L1/L2/L3 行已经有值单元格。

## Proposed Result Model

新增 row-based result model，同时保留旧顶层 Memory 字段。

建议 C# 模型：

```csharp
public sealed record MemoryBenchmarkRowResult(
    string Row,
    string DisplayName,
    bool Available,
    string? UnavailableReason,
    long? WorkingSetBytes,
    int? CacheLevel,
    long? CacheSizeBytes,
    int? LineSizeBytes,
    string? Source,
    MemoryBenchmarkMetricResult? Read,
    MemoryBenchmarkMetricResult? Write,
    MemoryBenchmarkMetricResult? Copy,
    MemoryBenchmarkMetricResult? Latency);
```

`MemoryBenchmarkResult` 增加：

```csharp
IReadOnlyDictionary<string, MemoryBenchmarkRowResult> Rows
```

Row key 使用稳定小写值：

```text
memory
l1
l2
l3
```

兼容规则：

- `ReadMiBS`、`WriteMiBS`、`CopyMiBS`、`LatencyNs` 继续表示 `Rows["memory"]` 的 median。
- 如果 native output 没有 `rows`，C# parser 从旧 `metrics` 构造 `memory` row。
- GUI 和 CLI 迁移到读取 `Rows`，但旧字段仍保留给简单调用者。

## Protocol Version 6

将 native worker protocol version 从 `5` 升到 `6`。

Final JSON 新增 `rows`：

```json
{
  "type": "result",
  "worker_version": "0.6.0",
  "protocol_version": 6,
  "rows": {
    "memory": {
      "display_name": "Memory",
      "available": true,
      "working_set_bytes": 536870912,
      "source": "options",
      "read": { "unit": "mib_s", "samples": [], "inner_iterations": [], "converged": true, "aggregate": {} },
      "write": {},
      "copy": {},
      "latency": {}
    },
    "l1": {
      "display_name": "L1 Cache",
      "available": true,
      "cache_level": 1,
      "cache_size_bytes": 32768,
      "line_size_bytes": 64,
      "working_set_bytes": 24576,
      "source": "windowsTopology",
      "read": {},
      "write": {},
      "copy": {},
      "latency": {}
    },
    "l2": {},
    "l3": {}
  }
}
```

Unavailable row example:

```json
{
  "display_name": "L3 Cache",
  "available": false,
  "unavailable_reason": "Windows topology did not return a unified L3 cache for the selected processor.",
  "source": "windowsTopology"
}
```

For compatibility during migration, native output may keep the existing top-level `metrics` object for Memory. C# should treat `rows.memory` as authoritative when present.

## Progress JSON

Progress metric events need row identity:

```json
{"type":"metric","row":"l1","metric":"read","value":123456.00,"unit":"mib_s"}
```

Compatibility:

- If `row` is missing, Core treats it as `memory`.
- `MemoryBenchmarkProgress` adds `Row` or `RowKind`.
- GUI maps row + metric to a specific table cell.

Recommended progress sequence:

```text
started
memory/read
memory/write
memory/copy
memory/latency
l1/read
l1/write
l1/copy
l1/latency
l2/read
...
result
completed
```

If a row is unavailable, emit an optional row status event:

```json
{"type":"row","row":"l3","available":false,"reason":"..."}
```

This event is optional for first implementation. Final JSON must remain authoritative.

## Cache Working Set Planning

Planning should happen in Core because Core already reads Windows topology and can pass explicit row plans to native worker.

### Selected Processor

Use the existing preferred single-thread placement heuristic:

- highest Windows efficiency class when heterogeneous classes exist;
- first SMT unit;
- preferred local NUMA node;
- middle physical core within the preferred set.

Cache rows should run on this selected logical processor. This keeps L1/L2 cache measurements local and keeps L3 domain selection explainable.

### Cache Matching

Use `LogicalProcessorInformation.TryCollect()`:

- L1: prefer level 1 Data cache whose mask contains selected processor.
- L2: prefer level 2 Unified cache whose mask contains selected processor.
- L3: prefer level 3 Unified cache whose mask contains selected processor.

If multiple candidates match:

- choose the smallest matching L1 Data cache;
- choose the smallest matching L2 Unified cache;
- choose the largest matching L3 Unified cache for the selected processor's LLC domain.

If a cache level is missing, mark that row unavailable instead of hardcoding a guessed size.

### Working Set Size

Working set should fit comfortably inside the target cache, not at the boundary. Boundary-sized tests are more likely to spill into the next level and make results noisy.

Initial policy:

```text
L1: max(8 KiB, min(cache_size * 3 / 4, cache_size - line_size))
L2: max(64 KiB, cache_size * 3 / 4)
L3: max(2 MiB, cache_size / 2)
```

Clamp rules:

- align working set down to a multiple of 64 bytes;
- keep at least 2 pointer nodes for latency;
- for `copy`, allocate source and destination buffers each with `working_set_bytes`; document that the payload working set is per buffer and total touched cache footprint is approximately 2x for copy.

For L1 copy, the 2x source+destination footprint can exceed L1. This should be documented in diagnostics. A later SIMD/cache-specific kernel can refine this.

## Native Worker Changes

### Options

Add cache row plan arguments. Prefer one repeatable argument per row:

```text
--cache-row l1:24576:1:32768:64:windowsTopology
--cache-row l2:786432:2:1048576:64:windowsTopology
--cache-row l3:16777216:3:33554432:64:windowsTopology
```

Format:

```text
row:workingSetBytes:cacheLevel:cacheSizeBytes:lineSizeBytes:source
```

Native direct invocation without `--cache-row` should still run only Memory row.

### Internal Refactor

Refactor `main.cpp` around row execution:

- Extract metric result storage into a reusable row structure.
- Keep existing adaptive sampling helpers.
- Keep existing scalar read, CRT memset write, CRT memcpy copy, and pointer-chasing latency kernels.
- Add `run_single_thread_row(rowPlan, options, timer)` for L1/L2/L3.
- Keep current multi-thread Memory path for `memory`.

Suggested native structs:

```cpp
struct RowPlan {
    std::string row;
    std::uint64_t working_set_bytes;
    int cache_level;
    std::uint64_t cache_size_bytes;
    int line_size_bytes;
    std::string source;
    bool available;
    std::string unavailable_reason;
};

struct RowResult {
    RowPlan plan;
    SampleSeries read;
    SampleSeries write;
    SampleSeries copy;
    std::vector<double> copy_traffic_samples;
    SampleSeries latency;
};
```

### Execution Order

1. Apply preferred affinity for the current thread.
2. Run Memory row exactly as today.
3. Re-apply preferred affinity before cache rows.
4. Run L1, L2, L3 single-thread rows in increasing level order.
5. Print final row-based JSON.

Running Memory first preserves current behavior and progress expectations. Running cache rows after Memory avoids changing Memory warmup and worker pool behavior.

### Latency

Cache latency should continue using pointer chasing. Each row gets a separate pointer chain sized to its working set.

Use existing adaptive policy:

- `base_steps = latency_steps / min_samples`
- scale `inner_iterations` until target sample duration is reached.

For very small L1 working sets, the adaptive loop should quickly increase inner iterations; this is expected.

## Core Changes

### Placement Planner

Extend `MemoryBenchmarkPlacementPlanner` to produce:

- existing worker placement plan for Memory;
- selected single-thread processor for cache rows;
- cache row plans for L1/L2/L3.

Potential model:

```csharp
internal sealed record MemoryBenchmarkCacheRowPlan(
    string Row,
    bool Available,
    string? UnavailableReason,
    long WorkingSetBytes,
    int? CacheLevel,
    long? CacheSizeBytes,
    int? LineSizeBytes,
    string Source);
```

The runner passes available row plans to native. Unavailable plans can either be:

- passed to native for final JSON inclusion; or
- added by Core during enrichment.

Recommendation: let Core add unavailable rows during enrichment. Native should only execute available row plans.

### Argument Building

`BuildArguments` adds `--cache-row` for available L1/L2/L3 plans.

Protocol version constant becomes `6`.

### Parsing

Parser changes:

- parse `rows` when present;
- parse row metric fields with existing `ParseMetricResult`;
- parse copy traffic metrics with existing copy parser;
- construct old top-level fields from `rows["memory"]`;
- fallback to old `metrics` for protocol 5-style JSON.

### Quality Flags

Extend quality evaluation:

- mark high CV per row;
- add row-level diagnostics later if needed;
- keep existing top-level flags based on Memory row to avoid over-warning the user.

Possible new flags:

```text
cacheRowUnavailable
cacheTopologyIncomplete
cacheWorkingSetEstimated
```

Use these conservatively. Missing L3 on unusual topology should not be treated as benchmark failure.

## GUI Changes

### XAML

Add value cells for L1/L2/L3 rows in `MemoryBenchmarkWindow.xaml`.

Required named cells:

```text
L1ReadText, L1WriteText, L1CopyText, L1LatencyText
L2ReadText, L2WriteText, L2CopyText, L2LatencyText
L3ReadText, L3WriteText, L3CopyText, L3LatencyText
```

Memory row cell names can stay unchanged.

### Progress Updates

`UpdateBenchmarkProgress` should switch by `progress.Row` and `progress.Metric`.

Unavailable rows:

- final result rendering should show `N/A`;
- diagnostics should include unavailable reason;
- progress does not need to show unavailable rows unless row status events are implemented.

### Final Rendering

After `RunAsync` completes:

- render every row from `result.Rows`;
- use decimal `MB/s` display for throughput, same as current Memory row;
- use `ns` for latency;
- keep diagnostics button enabled.

### Diagnostics

Diagnostics should include a section per row:

```text
Rows

Memory
WorkingSetBytes : 536870912
Read            : ...

L1 Cache
CacheSizeBytes  : 32768
LineSizeBytes   : 64
WorkingSetBytes : 24576
Source          : windowsTopology
Read samples    : ...
```

## CLI Changes

Change memory benchmark CLI output to a table:

```text
Memory Benchmark
----------------
             Read       Write      Copy       Latency
Memory       75234      68120      60211      87.4 ns
L1 Cache     900000     700000     650000     1.2 ns
L2 Cache     ...
L3 Cache     ...
```

Then keep current metadata lines:

```text
Worker  :
Timer   :
Core    :
Options :
Elapsed :
Quality :
```

If a row is unavailable, print `N/A`.

## Documentation Updates

After implementation, update:

- `README.md`
- `src/HwScope.Native.MemoryBench/README.md`
- `docs/memory-benchmark-design.md`

The docs should state:

- L1/L2/L3 rows are real cache working-set rows.
- Cache rows are single-threaded preferred-core tests in this stage.
- Memory row remains topology-aware multi-threaded by default.
- Results still should not be directly treated as AIDA64-equivalent because kernels are scalar/CRT.

## Validation Plan

### Build

```powershell
.\src\HwScope.Native.MemoryBench\scripts\build-msvc.ps1
dotnet build
```

### Native Quick Tests

Small sample smoke test:

```powershell
.\src\HwScope.Native.MemoryBench\build\Release\membench.exe --size-mib 16 --min-samples 2 --max-samples 3 --target-sample-ms 10 --latency-steps 1000 --expected-protocol-version 6 --json
```

Cache row direct test after Core-style row arguments are available:

```powershell
.\src\HwScope.Native.MemoryBench\build\Release\membench.exe --size-mib 16 --cache-row l1:24576:1:32768:64:manual --min-samples 2 --max-samples 3 --target-sample-ms 10 --latency-steps 1000 --expected-protocol-version 6 --json
```

### CLI

```powershell
dotnet run --project .\src\HwScope.Cli\HwScope.Cli.csproj -- benchmark memory
```

Expected checks:

- output includes Memory, L1 Cache, L2 Cache, L3 Cache rows;
- unavailable rows print `N/A` rather than crashing;
- metadata still prints worker/protocol/timer/placement/quality.

### GUI

Manual checks:

- open benchmark window from toolbar, navigation, and menu;
- progress fills Memory and cache cells without freezing UI;
- final values stay visible;
- Diagnostics shows row metadata, samples, CV, placement and environment;
- unavailable rows show `N/A` or blank with diagnostic reason.

### Sanity Checks

Do not assert exact values, but verify broad ordering:

- L1 latency should usually be lower than L2, L3, and Memory.
- L2 latency should usually be lower than L3 and Memory.
- Cache throughput should usually be higher than Memory throughput.
- L3 results may vary strongly on hybrid, multi-CCD, V-Cache, virtualized, or power-limited systems.

## Implementation Sequence

1. Add row result models in Core and compatibility parser tests through manual sample JSON.
2. Add protocol 6 output shape in native worker while keeping Memory row behavior unchanged.
3. Add Core cache row planning from Windows topology.
4. Add native single-thread cache row execution.
5. Extend progress events with `row`.
6. Update CLI table output.
7. Update WPF table cells and diagnostics rendering.
8. Build and run native, CLI, and GUI validation.
9. Update user-facing docs after behavior is confirmed.

## Risks And Mitigations

### L1 Copy Footprint

Copy uses separate source and destination buffers, so the actual touched footprint can be roughly 2x payload size. L1 copy may spill into L2 even when each buffer is sized under L1.

Mitigation: document this in diagnostics and preserve copy traffic metadata. Later custom kernels can improve cache-local copy semantics.

### Windows Topology Gaps

`GetLogicalProcessorInformationEx` is OS topology, not full CPUID. Some systems may omit or simplify cache details.

Mitigation: mark unavailable rows clearly. Do not invent cache sizes.

### AIDA64 Comparison

AIDA64 likely uses highly optimized CPU-specific kernels. HwScope cache rows will initially use scalar/CRT paths.

Mitigation: keep kernel metadata explicit and avoid claiming score equivalence.

### Protocol Migration

Changing protocol can break old native binaries.

Mitigation: Core passes `--expected-protocol-version 6`; mismatch fails clearly. Parser keeps protocol 5 fallback for diagnostics and staged development.
