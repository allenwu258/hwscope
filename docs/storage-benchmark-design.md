# Storage Benchmark Requirements And UI Design

本文档定义 HwScope 硬盘跑分模块的产品需求、测量语义、安全边界、领域模型和 UI 设计，并记录首个实现的完成状态。

用户提供的参考截图来自 `CrystalDiskMark 8.0.4 x64`，不是 CrystalDiskInfo。CrystalDiskInfo 是设备身份与 SMART/Health 工具，CrystalDiskMark 才是截图中的存储性能测试工具。HwScope 应吸收 CrystalDiskMark 的测试矩阵、参数密度和操作效率，但不复制绿色渐变皮肤，也不假定两者结果可以直接横向等同。

## Current Implementation Status

截至 2026-07-15，首个可运行实现已经落地：

- `HwScope.Core.Benchmark.Storage` 已包含 target discovery、volume extent、options/plan、checked write budget、preflight、progress/result、quality、formatter 和 process runner。
- `HwScope.Native.StorageBench` 已实现 create-new 文件准备、64 MiB 对齐确定性随机写入池、设备/系统缓存模式、IOCP overlapped I/O、四行 Read/Write/Mix、每轮统计、协议 v1、协作取消和 cleanup。
- Core runner 已实现 volume GUID/serial/single-extent 复核、目录祖先 reparse 检查、目标卷级 lock file、10 分钟默认 timeout、2 秒 cooperative cancel grace、process-tree kill、严格 result contract、父进程兜底删除和脱敏诊断日志。
- `%LOCALAPPDATA%\HwScope\StorageBench\Manifests` 保存 active session；worker 在 create-new 后记录 volume serial 和 128-bit file ID，窗口只清理同时通过 GUID/path/root/size/reparse-point/file-identity 复核的残留。
- `StorageBenchmarkWindow` 已接入工具菜单、顶部工具栏和 `性能测试 -> 存储跑分`，包含参数、目标/预算条、四行三列矩阵、进度、取消、Diagnostics、Copy 和 TXT/JSON Save。
- CLI 已支持 `benchmark storage`、target/quick/size/runs/workload/JSON 参数；所有运行必须显式传入 `--drive`，不会回退到系统卷；`--cancel-after-ms` 用于取消路径诊断。
- App 提供 `HWSCOPE_STORAGE_BENCHMARK_PREVIEW=1` 开发预览入口，可绕过完整硬件 preload 做窗口级 UI 验证。
- 打开窗口和切换目标卷只消费已有存储详情缓存，不主动触发 SMART/Health 查询或唤醒休眠 HDD；主动温度刷新只发生在用户已明确启动跑分之后。
- Core 当前共 56 项测试通过，其中 23 项覆盖存储跑分 planner、preflight、worker result contract 和 session cleanup。
- 本机已验证 64 MiB `SEQ1M Q1T1`/`SEQ1M Q8T1` device-mode 完成路径、Read -> Write -> Mix 事件顺序、CLI 缺少显式目标时拒绝启动 worker，以及运行中取消路径；验证结束后测试文件和 manifest 都为 0。

仍未完成：durable/write-through 模式、后台 I/O 可靠检测、BitLocker/电源 quality flag、fresh before-run 温度快照、Storage Spaces/RAID、USB/SATA/HDD 广覆盖和长期 sustained/steady-state workload。当前结果不能直接等同于 CrystalDiskMark。

## Current Baseline

### Memory Benchmark

当前内存跑分已经形成以下链路：

```text
WPF / CLI
  -> IMemoryBenchmarkRunner
  -> MemoryBenchmarkProcessRunner
  -> native membench.exe worker
  -> newline-delimited progress JSON
  -> final structured JSON
  -> Core enrichment / quality evaluation
  -> result matrix / diagnostics
```

可复用的工程模式：

- 独立 `FluentWindow`，由 `SingleInstanceWindowManager` 管理单实例窗口。
- Core 持有 runner、options、progress、result、quality 和 formatter，不让 WPF 直接解析 native 输出。
- native worker 运行在独立进程，崩溃、超时和取消不会拖垮 GUI。
- stdout 使用逐行 JSON 上报进度，最终 `result` JSON 是权威结果。
- Core 与 worker 双向校验 protocol version。
- timeout/cancel 会终止完整 process tree，并保存 stdout/stderr 诊断日志。
- 结果保存 raw samples、median/min/max/mean/stddev/CV、环境、运行参数、worker version 和 elapsed time。
- GUI 提供独立 Diagnostics 窗口，CLI 可以复用同一 runner。
- 独立窗口必须在 `Loaded` 后接入主题服务。

内存跑分不应原样复制的部分：

- `MemoryBenchmarkPlacementPlanner` 面向 CPU topology、NUMA 和线程亲和性，不适用于目标卷和文件 I/O 规划。
- 当前内存窗口主要使用默认参数；存储跑分必须在开始前显示目标卷、文件大小、模式和写入量。
- 只在 metric 完成后上报进度不够；存储测试需要准备、样本、字节进度、取消和清理阶段。
- 存储测试会创建并写入真实文件，错误恢复和残留文件治理比内存缓冲区更重要。

### Storage Detail

现有存储详情模块已经提供：

- 启动 inventory 中的物理磁盘身份和稳定 ID。
- 物理磁盘、分区、卷、盘符、文件系统、容量和可用空间映射。
- Windows Storage descriptor、bus、logical/physical sector size 和 TRIM 信息。
- NVMe Health 与 ATA SMART 的温度、寿命和错误状态。
- `StorageDetailService` 的按设备缓存、并发请求合并、soft timeout 和迟到结果保护。

这些数据可以作为跑分前后的只读设备快照，但不能承担 benchmark 执行：

```text
StorageDetailService             StorageBenchmarkRunner
----------------------------     --------------------------------
设备身份 / 健康 / 温度            目标卷实时校验 / 测试文件生命周期
按物理磁盘读取                    按可写卷或目录执行
只读 provider                     有预算的文件读写
UI soft timeout                   可终止的独立 worker
```

跑分 runner 必须在开始前重新解析卷和 physical extents，不能只信任详情页缓存。盘符、挂载点、可用空间和设备连接状态都可能在选择后发生变化。

## Product Goals

- 让普通用户通过一个紧凑窗口完成可靠的顺序与随机读写测试。
- 让高级用户明确知道测试了哪个卷、哪块设备、使用何种缓存和持久化语义。
- 使用与 CrystalDiskMark 易于对应的四行 workload，降低理解和比较成本。
- 同时报告吞吐、IOPS 和延迟，不让 4K 随机能力被一个 MB/s 数字掩盖。
- 在运行前给出可用空间、测试文件大小和预计最大写入量。
- 保证所有写入只发生在 HwScope 新建的临时测试文件中。
- 对取消、超时、worker 崩溃、设备拔出和应用重启提供可解释的清理与恢复。
- 保留足够的 raw samples、环境和质量标志，使结果可诊断、可导出、可复核。
- 与现有 HwScope 主题、窗口、导航、详情数据和 CLI 架构保持一致。

## Requirement Catalog

| ID | Priority | Requirement | Acceptance signal |
| --- | --- | --- | --- |
| `SB-F01` | P0 | 枚举并选择本地可写卷，同时展示 backing device | UI、preflight 与 result 的 volume/device identity 一致 |
| `SB-F02` | P0 | 支持标准四行 Read/Write workload | 四行能独立运行，也能由 `全部开始` 顺序运行 |
| `SB-F03` | P1 | 支持可选 Mix 与固定 R/W ratio | result 保存总量与 read/write split |
| `SB-F04` | P0 | runs、file size、unit、cache mode 和测试列可配置 | plan 和导出保存实际生效值 |
| `SB-F05` | P0 | 按阶段、workload、sample 和 bytes 上报进度 | UI 不需要从日志文本猜测进度 |
| `SB-F06` | P0 | 显示 MB/s、IOPS、latency 和 raw runs | 主表与 Diagnostics 使用同一 Core result |
| `SB-F07` | P0 | 支持取消、超时、窗口关闭和 worker crash | 所有路径都有有界停止与 cleanup 结论 |
| `SB-F08` | P1 | Copy、TXT/JSON Save 和 Diagnostics | 导出可复现 target、plan、results 和 quality |
| `SB-S01` | P0 | 只允许 file-backed benchmark | 代码与协议不存在 physical-drive write 入口 |
| `SB-S02` | P0 | 只以 create-new 创建 session file | existing file 永不被打开为写入目标 |
| `SB-S03` | P0 | 运行前计算和显示最大写入量 | plan 超过 write budget 时无法开始 |
| `SB-S04` | P0 | 验证 path、volume、extent、space 和 alignment | 目标变化或不支持时在 I/O 前失败 |
| `SB-S05` | P0 | worker 和 parent 双层清理 | 正常、取消、超时与崩溃均覆盖测试 |
| `SB-S06` | P0 | 安全发现孤儿文件 | 只处理通过 manifest/目录/文件名/volume 复核的 session |
| `SB-R01` | P0 | 独立 worker 和协议版本校验 | native failure 不终止 GUI，版本不匹配拒绝运行 |
| `SB-R02` | P0 | final result 为唯一权威值 | incomplete progress stream 不生成成功成绩 |
| `SB-R03` | P1 | 保存环境和 quality flags | 缓存、温度、电源和 variance 可解释 |
| `SB-U01` | P0 | 第一屏完成配置、运行和横向比较 | 无需进入说明页或多层弹窗 |
| `SB-U02` | P0 | active/disabled/error/unsupported 状态稳定可读 | light/dark、高 DPI、最小窗口无重叠 |
| `SB-U03` | P1 | 完整键盘与辅助技术语义 | row command、progress 和 warning 有 accessible name/state |

## Non-Goals

首版明确不做：

- 不直接写 `\\.\PhysicalDriveN`，不执行 raw disk benchmark。
- 不覆盖、截断或复用用户已有文件。
- 不执行格式化、TRIM、secure erase、sanitize、firmware update 或 SMART self-test。
- 不把系统文件缓存成绩标记成设备真实性能。
- 不承诺与 CrystalDiskMark、AS SSD、厂商工具得出相同数字；不同 workload engine 和参数只能做趋势参考。
- 不在后台自动跑分，不定时唤醒 HDD，不在应用启动期创建测试文件。
- 不把 RAID、Storage Spaces 或网络路径的成绩伪装成某一块物理盘的成绩。
- 不在首版实现 sustained full-drive、steady-state、SLC cache exhaustion 或盘外 raw latency 测试。
- 不在首版建立云端排行榜或综合分数。

## Users And Core Workflows

### Quick Check

用户保持默认参数，确认目标卷和预计写入量，点击 `全部开始`。窗口依次完成四行测试，在每个单元格中显示结果，结束后可复制或保存报告。

### Read-Focused Check

用户选择 `仅读取`。如果本次会新建测试文件，界面必须说明准备文件仍会产生一次初始化写入；不能把“只测读取”描述成“绝不写盘”。

### Targeted Workload

用户点击某一行的 workload 按钮，只运行该行当前启用的 Read/Write/Mix 列。用于快速检查 `RND4K Q1T1` 或复测异常结果。

### Advanced Diagnosis

用户调整 runs、size、cache mode、测试列和 Mix 比例，运行后打开 Diagnostics，查看每轮结果、延迟分位数、设备/卷身份、实际字节量、温度变化、质量标志和 cleanup 状态。

### Interrupted Run

用户点击取消、关闭窗口，或外接盘被拔出。界面进入 `正在取消`/`正在清理`，worker 停止提交新 I/O，取消未完成 I/O，关闭 handle 并删除临时文件。若清理失败，明确显示残留路径的脱敏描述和后续清理入口。

## CrystalDiskMark Mapping

参考截图的主要结构是：

```text
Runs = 5
Size = 1 GiB
Target = C: 27% (518/1907 GiB)
Unit = MB/s
Mix = R70% / W30%

                Read       Write      Mix
SEQ1M Q8T1
SEQ1M Q1T1
RND4K Q32T1
RND4K Q1T1
```

HwScope 对应关系：

| CrystalDiskMark concept | HwScope design | Difference |
| --- | --- | --- |
| All | `全部开始` 主命令 | 运行时原位变为 `取消` |
| Run count | `Runs` ComboBox | 同时进入写入预算计算 |
| Test size | `Size` ComboBox | 显示实际字节与剩余空间约束 |
| Drive selector | 目标卷 selector | 同时显示物理设备、文件系统和 system/boot role |
| MB/s selector | 单位 selector | MB/s、GB/s、IOPS 只改变主显示，不丢失其他指标 |
| R70/W30 | Mix ratio | 只在启用 Mix 时可编辑 |
| Four workload rows | 同名四行矩阵 | workload 语义写入结果和导出，不只显示缩写 |
| Green progress background | 主题 accent 的单色进度轨道 | 不使用渐变，不用颜色作为唯一状态 |

## Benchmark Presets

首版提供三个 preset，默认选择 `标准`：

| Preset | Runs | File size | Warmup | Rows | Columns | Purpose |
| --- | ---: | ---: | ---: | --- | --- | --- |
| 快速 | 1 | 256 MiB | 0 | Q1 rows first, all rows optional | Read + Write | 快速健康检查 |
| 标准 | 5 | 1 GiB | 0 | four standard rows | Read + Write | 日常比较，默认 |
| 自定义 | 1/3/5/9 | 64 MiB-8 GiB | 0/1 | selectable | Read/Write/Mix | 诊断与复测 |

切换任一独立参数后，preset 显示 `自定义`。首版不隐藏高级参数的实际值，preset 只是可复现的参数集合。

默认不启用 Mix，避免在用户未注意时增加写入量。启用 Mix 后默认 `R70/W30`。

## Workload Definitions

### Common Terms

- `SEQ1M`：1 MiB block，按连续 offset 访问。
- `RND4K`：4 KiB block，在测试文件范围内使用确定性 seed 生成对齐随机 offset。
- `Qn`：每个 issuing thread 同时在途的最大 I/O request 数。
- `Tn`：issuing thread 数；首版四行均为一个 issuing thread。
- `Read`：只提交 read request。
- `Write`：只向当前 session 的测试文件提交 write request。
- `Mix`：在同一个 queue 中按指定比例交错 read/write request。

### Standard Matrix

| Workload ID | Block | Queue | Threads | Offset pattern | Primary meaning |
| --- | ---: | ---: | ---: | --- | --- |
| `seq1m-q8t1` | 1 MiB | 8 | 1 | sequential, wrap at EOF | 高队列顺序吞吐 |
| `seq1m-q1t1` | 1 MiB | 1 | 1 | sequential, wrap at EOF | 单队列顺序性能 |
| `rnd4k-q32t1` | 4 KiB | 32 | 1 | deterministic random | 高队列小块并发能力 |
| `rnd4k-q1t1` | 4 KiB | 1 | 1 | deterministic random | 交互型小块响应能力 |

每个 workload 的 block size、queue depth 和 thread count 都是结果合同的一部分。实现不能为了兼容某个设备而静默改变。例如设备要求的最小无缓存对齐大于 4 KiB 时，`RND4K` 应显示 `不支持当前对齐要求`，不能偷偷改成 8 KiB 后仍标为 `RND4K`。

### Planned Work Per Sample

首版采用固定字节计划，而不是无限制的定时写入：

- 每个 sample 的计划逻辑字节数默认为 test file size。
- 顺序访问从确定的起点连续运行，达到 EOF 后 wrap。
- 随机访问的 operation count 为 `fileSize / blockSize`。
- 每列执行 `runs` 个 measured samples，可在正式 sample 前执行一个短 warmup。
- warmup、文件初始化和 Mix 中的 write bytes 都计入写入预算。
- worker 达到 session 写入上限后必须停止，不允许为了收敛自动追加无上限样本。

固定字节计划使运行前能够给出最大写入量，也让取消后的最大额外写入被限制为已提交的 in-flight requests。

### Read, Write And Mix Ordering

`全部开始` 使用确定性顺序：

```text
preflight
prepare test file
Read:  SEQ1M Q8T1 -> SEQ1M Q1T1 -> RND4K Q32T1 -> RND4K Q1T1
Write: SEQ1M Q8T1 -> SEQ1M Q1T1 -> RND4K Q32T1 -> RND4K Q1T1
Mix:   SEQ1M Q8T1 -> SEQ1M Q1T1 -> RND4K Q32T1 -> RND4K Q1T1
cleanup
```

未启用的列跳过。结果记录实际执行顺序。首版不随机轮换顺序，因为这会降低复现性；热偏差通过温度快照、短 cooldown 和 quality flags 解释。

### Mix Semantics

- 默认比例为 `70% read / 30% write`。
- 比例按 operation count 计算，不按最终字节吞吐反推。
- 同一 workload 中 read/write 使用相同 block size 和 queue depth。
- 随机 Mix 使用固定 seed 的 operation schedule。
- 顺序 Mix 使用独立的 read cursor 和 write cursor，分别连续递增并在 EOF wrap。
- Mix 主值是总吞吐；结果同时保留 read/write bytes、吞吐和 IOPS 分量。

## Cache And Durability Semantics

缓存模式是结果可解释性的核心，不能只藏在 Diagnostics。

### Device Mode

UI 名称：`设备模式`，副说明：`禁用 Windows 文件缓存`。

- 使用 file-backed I/O 和 `FILE_FLAG_NO_BUFFERING`。
- buffer address、offset 和 length 必须满足卷/设备 alignment 要求。
- 设备自身 DRAM/SLC/write cache 仍可能生效，结果不得标成“无任何缓存”。
- 每个 measured sample 外执行必要的同步/flush；flush 时间是否计入必须在 result 中记录。
- 首版默认 flush 不计入吞吐，结果标记 `flushOutsideTimedRegion`。

这是首版默认模式，最接近用户对存储设备性能的预期，但不承诺复制 CrystalDiskMark 的内部 engine。

### Buffered Mode

UI 名称：`系统缓存`，副说明：`包含 Windows 文件缓存影响`。

- 使用普通 file I/O，不设置 `FILE_FLAG_NO_BUFFERING`。
- 结果永久带 `osCacheEnabled` quality flag。
- UI 在主结果区显示 `系统缓存` 标签。
- buffered 与 device mode 结果不能在同一矩阵中混合。

该模式用于诊断应用层文件 I/O，不应作为默认设备性能成绩。

### Deferred Durable Mode

`write-through`、每请求持久化、每批 `FlushFileBuffers` 计时属于后续模式。首版不能用模糊的 `Direct` 名称把 no-buffering、write-through 和 raw physical I/O 混为一谈。

## Test Data And File Preparation

- 使用不可压缩倾向的确定性伪随机数据，不使用全零块。
- worker 在计时前生成最多 64 MiB 的对齐数据池；写 I/O 直接引用 immutable block，不在 timed region 生成或复制整块数据。
- 每个 sample 或 pass 使用不同 seed 改变数据池 block 起点，重复周期不短于数据池大小，降低控制器压缩/去重造成的虚高。
- 当前 protocol v1 不导出 seed；如后续需要跨版本精确复现，应先扩展 result contract 和 protocol version。
- 测试文件必须完整初始化后再执行 read workload，避免 sparse/unallocated extent 返回零页。
- 不使用 `SetFileValidData`，避免权限依赖和读取未初始化磁盘内容的安全问题。
- 不主动发送 TRIM；文件删除后的 trim 行为由文件系统和 Windows 决定。
- 初始化耗时不计入 workload 成绩，但初始化字节计入预计/实际写入量。

## Target Model

用户选择的是可写卷，worker 最终操作的是该卷上的专用目录和新文件。领域模型必须同时保留：

```text
StorageBenchmarkTarget
  volumeGuidPath
  selectedMountPath
  testDirectory
  driveLetter / label / fileSystem
  sizeBytes / freeBytes
  roles: system / boot / pagefile / removable
  diskExtents[]
  physicalDeviceStableIds[]
  bus / media / protocol summary
  logicalSectorBytes / physicalSectorBytes / requiredAlignment
  identityCapturedAt
```

目标 selector 优先显示本地、已挂载、可写卷。显示格式：

```text
C:  Samsung PM9F1  |  NTFS  |  可用 518 GiB / 1.86 TiB  |  系统盘
E:  External SSD   |  exFAT |  可用 742 GiB / 931 GiB  |  USB
```

没有盘符但有可写 mount path 的卷可以显示实际 mount path。网络 share 不是首版目标。

### Multi-Extent And Virtual Volumes

开始前通过 volume extents 重新确认 backing disks：

- 单一 physical extent：可关联存储详情快照和温度。
- Storage Spaces、RAID、striped/dynamic multi-extent：首版默认不支持；UI 说明成绩无法归属单盘。
- VHD/VHDX、RAM disk：首版拒绝或明确标记 `virtualTarget`，不能展示为物理盘结果。
- BitLocker 卷允许测试，但显示 `包含加密层开销` quality flag。

## Test Directory And File Lifecycle

### Directory Resolution

默认测试目录必须与目标卷相同：

1. 系统卷优先使用该卷上的 `%LOCALAPPDATA%\HwScope\StorageBench\Sessions`。
2. 非系统卷尝试 `<mount-root>\HwScope-Benchmark`。
3. 默认目录不可写时，UI 提供 `选择文件夹`，并再次验证所选目录仍位于目标卷。

测试文件名：

```text
hwscope-storagebench-{session-guid}.tmp
```

创建必须使用 create-new/exclusive 语义。任何已存在文件都视为冲突，生成新的 GUID；禁止 open-or-create、truncate-existing 或用户自定义文件名。

### Session Manifest

每次运行只在应用数据目录保存 session manifest；目标目录不创建独立 sidecar marker。当前 manifest 字段与 `SessionManifest` 一致：

```text
CreatedBy
SessionId
TargetRoot
TestDirectory
TestFilePath
PlannedFileSizeBytes
CreatedAt
VolumeSerialNumber
FileId
```

manifest 先以空文件身份创建；worker 用 `CREATE_NEW` 打开测试文件后发送 `file_created`，Core 再把 volume serial 和 128-bit file ID 原子写回 manifest。下次启动的 orphan cleanup 不会删除缺少文件身份的残留；同一次运行中的 parent fallback 仍可按当前 plan 的精确文件名和目录做 best-effort 清理。manifest 不保存完整设备序列号，也不能作为信任任意路径并删除的授权。

### Cleanup Rules

- 正常完成、取消、超时、worker error 都执行 cleanup。
- worker 首先 cooperative cancel：停止提交、`CancelIoEx`、等待在途 I/O、关闭 handle、删除文件。
- 超过 grace period 后，Core 终止 process tree，再按 manifest 做 parent-side best-effort cleanup。
- 删除前重新验证文件名、目录、session ID、volume ID、128-bit file ID、reparse 属性和大小边界；同路径替换文件必须拒绝删除。
- 不递归删除用户目录，不跟随 reparse point，不删除不满足 HwScope session 格式的文件。
- cleanup 失败写入 result/error，并在下次打开跑分窗口时显示可操作的残留提示。
- orphan 扫描只检查 HwScope 自己的 manifest 和专用目录，不扫描整个磁盘。

## Safety And Write Budget

### Preflight

开始前必须完成：

- 目标卷仍存在，mount path 与 volume ID 未变化。
- 目标目录可创建、可写，且不经过 network redirector。
- 文件系统支持请求的文件大小和 I/O 语义。
- 卷不是只读、offline、dirty/错误状态或已知虚拟/RAM target。
- physical extents 与 UI 展示一致。
- file size、block size 和 alignment 合法。
- 可用空间足够，并保留安全余量。
- 预计最大写入量不超过 session write budget。
- 同一目标卷没有另一个 HwScope benchmark session。
- worker protocol 与 Core 匹配。

建议首版空间限制：

```text
default file size          1 GiB
allowed file size          64 MiB, 256 MiB, 1 GiB, 4 GiB, 8 GiB
file size upper bound      min(8 GiB, 10% of current free space)
free space after reserve   at least max(2 GiB, 5% of volume size)
default session write cap  64 GiB
absolute configurable cap  512 GiB
```

小容量卷无法满足通用余量时，不静默降低 file size；UI 给出可选的安全 size。

### Write Estimate

运行前计算并显示：

```text
maximumWriteBytes =
    fileInitializationBytes
  + writeWarmupBytes
  + writeMeasuredBytes
  + mixWarmupWriteBytes
  + mixMeasuredWriteBytes
  + alignmentSafetyMargin
```

例：默认标准 preset 为 `1 GiB / 5 runs / 0 warmup / four rows / Read+Write`，其计划值是 `20 GiB measured writes + 1 GiB file preparation`，因此 UI 显示 `预计最多写入 21 GiB（含 1 GiB 文件准备）`。启用 warmup 或 Mix 后必须重新计算；具体值来自 workload planner，不能在 UI 中写死。

运行中 worker 维护实际 logical bytes read/written，并且只在 I/O completion 成功后累计；超过 budget 前停止提交新 I/O。protocol v1 不另设一套 OS-acknowledged bytes 字段。SSD NAND 实际写放大无法由应用准确得知，不应伪装为可测值。

### System And External Drives

- 系统盘：允许测试，目标条显示 `系统盘`，并提示前台负载会影响系统响应和结果。
- pagefile/boot 卷：允许但增加 quality flag。
- USB/removable：允许本地可写单盘卷；拔出时停止并报告 `deviceRemoved`。
- HDD：不自动启动；选择高 QD random 时显示预计较慢的 inline note。
- 电池供电：允许但显示 `onBatteryPower`，默认建议接通电源。
- 睡眠/休眠：运行时使用 scoped execution-state request，结束后必须释放；不修改系统电源计划。

## Proposed Architecture

硬盘跑分建立独立模块，不扩展 `membench.exe`：

```text
HwScope.App
  StorageBenchmarkWindow
  StorageBenchmarkDiagnosticsWindow
  target selector / matrix / state rendering

HwScope.Core.Benchmark.Storage
  IStorageBenchmarkRunner
  StorageBenchmarkOptions / Plan / Target
  StorageBenchmarkProgress / Result / Quality
  StorageBenchmarkPreflight
  StorageBenchmarkProcessRunner
  StorageBenchmarkResultFormatter

HwScope.Native.StorageBench
  file-backed Windows I/O worker
  alignment / async queue / latency histogram
  progress JSON / final result JSON
  cooperative cancel / cleanup
```

依赖方向：

```text
App -> Core storage benchmark domain -> worker process
App -> StorageDetailService (before/after optional snapshots)

worker -X-> StorageDetailService
worker -X-> WPF
StorageDetailService -X-> benchmark execution
```

独立 worker 的理由：

- 文件 I/O、queue depth、overlapped completion 和缓存 flag 与内存 kernel 无关。
- 存储写入有更严格的目标路径、空间、取消和残留文件风险。
- worker 崩溃或驱动异常不能拖垮 WPF。
- Core 可以在 worker 被强制终止后继续清理和保存诊断。

## Domain Model

建议的 Core 合同：

```csharp
public sealed record StorageBenchmarkOptions(
    int Runs,
    long FileSizeBytes,
    StorageBenchmarkCacheMode CacheMode,
    StorageBenchmarkColumnMode Columns,
    int MixReadPercent,
    long WriteBudgetBytes,
    IReadOnlyList<StorageWorkloadId> Workloads,
    int WarmupPasses,
    TimeSpan Cooldown,
    TimeSpan Timeout);

public sealed record StorageBenchmarkPlan(
    string SessionId,
    StorageBenchmarkTarget Target,
    StorageBenchmarkOptions Options,
    IReadOnlyList<StorageWorkloadPlan> Workloads,
    long PlannedReadBytes,
    long MaximumWriteBytes,
    string TestFilePath);

public sealed record StorageWorkloadPlan(
    StorageWorkloadId Id,
    StorageOperationMode Operation,
    int BlockSizeBytes,
    int QueueDepth,
    int Threads,
    long BytesPerSample,
    int Samples,
    ulong Seed);
```

Result 不应只有矩阵上的一个数字：

```csharp
public sealed record StorageMetricResult(
    string Unit,
    IReadOnlyList<StorageSampleResult> Samples,
    StatisticalAggregate Throughput,
    StatisticalAggregate Iops,
    LatencyAggregate Latency,
    long LogicalBytesRead,
    long LogicalBytesWritten,
    bool Converged);

public sealed record LatencyAggregate(
    double MeanMicroseconds,
    double P50Microseconds,
    double P95Microseconds,
    double P99Microseconds,
    double MaximumMicroseconds,
    IReadOnlyList<LatencyHistogramBucket> Histogram);
```

顶层 result 至少保存：

- protocol/worker/Core version。
- plan、实际 target identity、开始/结束时间、elapsed。
- 每行 Read/Write/Mix metric。
- raw run samples 和 aggregate。
- read/write bytes 与写入预算使用量。
- test file preparation、flush 和 cleanup 状态。
- volume/free space before/after。
- physical device identity 和 extents before/after。
- temperature/health before/after，获取失败不使成绩失败。
- OS、power plan、AC state、process priority、cache mode、alignment。
- quality flags、warnings 和 error classification。

## Measurement And Statistics

- 吞吐使用 decimal `MB/s = bytes / 1,000,000 / seconds`。
- `GB/s` 也是 decimal；测试文件大小仍使用 MiB/GiB binary unit。
- `IOPS = completed operations / measured seconds`。
- request latency 从提交到 completion，包含该 request 在 worker queue 中的等待，不包含 workload 准备时间。
- 每轮输出 throughput、IOPS、latency quantiles、bytes、operation count 和 elapsed。
- 主表使用 runs 的 median，Diagnostics 展示 min/max/mean/stddev/CV。
- latency 使用固定上限 histogram/streaming quantile 结构，不能把数百万个 request timestamp 全部保存在内存中。
- 不因为 CV 未收敛而自动无限加样本；固定 runs 结束后标记 `highVariance`。
- 极短 sample 标记 `sampleTooShort`，建议增大 file size。

### Quality Flags

至少定义：

```text
highVariance
sampleTooShort
osCacheEnabled
flushOutsideTimedRegion
deviceCacheMayBeActive
backgroundIoSuspected
thermalChangeDetected
temperatureUnavailable
onBatteryPower
systemVolume
pageFileVolume
bitLockerEnabled
virtualTarget
multiExtentTarget
targetIdentityChanged
freeSpaceChangedUnexpectedly
cleanupIncomplete
workerForcedTermination
deviceRemoved
alignmentFallback
```

quality flag 不应擅自“修正”成绩，只解释结果是否适合比较。

## Progress Protocol

stdout 使用 newline-delimited JSON。progress/lifecycle 事件包含 `protocol_version`、`session_id`、单调递增 `sequence` 和 `type`；最终 `result` 是独立的权威 payload，不带 progress sequence。

当前 protocol v1 事件：

```text
started
phase                    # preflight / preparing / cleanup
file_created             # volume_serial_number / file_id
workload_started
workload_progress
sample_completed
workload_completed
result
completed
```

`workload_progress` 包含：

```text
workload_id / operation
sample_index / sample_count
completed_bytes / planned_bytes
```

`sample_completed` 另外包含 `throughput_mb_s`、`iops` 和 `p95_microseconds`；UI 只把这两类 sample/progress 事件用于更新 cell 文本，workload lifecycle 事件不能覆盖已完成 sample。

规则：

- `result` 是成功运行的唯一权威结果；UI progress 值不能拼装成最终 report。
- 成功运行必须包含一次 `file_created`、一次 `result` 和后续 `completed`；缺失或重复身份事件都视为协议错误。
- `completed` 只在 result 已完整输出且 cleanup 状态已确定后发送。
- stdout 只输出协议 JSON，诊断文字进入 stderr。
- Core 对未知的可选事件向前兼容，对未知 protocol version 拒绝运行。
- progress/lifecycle event 缺少 sequence、sequence 不递增、跨 session 或 terminal events 缺失都视为 protocol error；sequence 不要求连续无间隔。
- Core 对 result 的 cache mode、row definition、启用/禁用 operation、sample 数量/索引、有限非负指标、读写字节和 cleanup 一致性逐项匹配 plan。

取消使用 stdin control message 或等价的受控 IPC：Core 先请求 cooperative cancel，等待短 grace period，再 kill process tree。worker 在每批提交间检查取消，并对已提交的 overlapped I/O 调用 `CancelIoEx`。

## Error Model

错误必须分类，UI 不直接展示 native exception/Win32 stack：

```text
InvalidOptions
TargetNotFound
TargetChanged
UnsupportedTarget
ReadOnlyVolume
InsufficientSpace
WriteBudgetExceeded
AlignmentUnsupported
AccessDenied
FileCreateFailed
IoError
DeviceRemoved
Timeout
Canceled
CleanupFailed
WorkerNotFound
ProtocolMismatch
WorkerCrashed
EnvironmentCollectionFailed
```

一个 workload 不支持时可以显示单元格级 `Unsupported`；目标消失、文件 I/O error 或 identity 变化应终止整个 session。失败结果仍应包含已经完成的 samples、实际写入量、错误阶段和 cleanup 状态，但不能冒充完整 benchmark result。

## Window Information Architecture

硬盘跑分使用独立窗口，与内存跑分一致由 single-instance manager 管理。主窗口入口位于：

- 左侧导航：`性能测试 -> 存储跑分`。
- 顶部快捷工具栏：已增加独立 `存储跑分`，并将原有 `跑分` 明确为 `内存跑分`。
- 存储详情页：可选的 `测试此卷` 命令，只负责预选目标，不在详情页内嵌 benchmark。

打开来源带有物理设备选择时：

- 设备只有一个可写卷：直接预选。
- 设备有多个可写卷：预选可用空间最大的非隐藏卷，仍允许用户切换。
- 没有可写卷：窗口打开但 Start disabled，说明必须选择有文件系统的可写卷。

## Desktop UI

建议默认窗口 `1120 x 760`，最小尺寸 `860 x 620`。第一屏直接是工具，不增加 hero、欢迎页或说明卡片。

```text
┌ Storage Benchmark ──────────────────────────────────────────────────────┐
│ [全部开始 ▶]  次数 [5⌄]  大小 [1 GiB⌄]  目标 [C: · Samsung...⌄]        │
│               单位 [MB/s⌄]  模式 [设备模式⌄]  测试 [读+写⌄]  [设置 ⚙] │
├─────────────────────────────────────────────────────────────────────────┤
│ Samsung PM9F1 · NVMe · NTFS · C: · 系统盘                              │
│ 可用 518 GiB / 1.86 TiB · 文件 1 GiB · 预计最多写入 21 GiB             │
│ [禁用 Windows 文件缓存] [设备缓存可能生效]                 47 C         │
├──────────────┬──────────────────┬──────────────────┬────────────────────┤
│              │ Read             │ Write            │ Mix R70 / W30      │
├──────────────┼──────────────────┼──────────────────┼────────────────────┤
│ [SEQ1M Q8T1] │        7103.83   │        6872.23   │           --       │
│ 1 MiB · Q8   │ MB/s  6.77 GiB/s│ p95 1.18 ms      │ 未启用             │
├──────────────┼──────────────────┼──────────────────┼────────────────────┤
│ [SEQ1M Q1T1] │        4037.95   │  64%   5501.89   │           --       │
│ 1 MiB · Q1   │ MB/s  p95 .25 ms│ 正在运行 3/5     │ 未启用             │
├──────────────┼──────────────────┼──────────────────┼────────────────────┤
│ [RND4K Q32T1]│         495.37   │         386.75   │           --       │
│ 4 KiB · Q32  │ 126.8K IOPS     │ 99.0K · p95 .8ms │ 未启用             │
├──────────────┼──────────────────┼──────────────────┼────────────────────┤
│ [RND4K Q1T1] │          83.90   │         195.84   │           --       │
│ 4 KiB · Q1   │ 21.5K · p95 52us│ 50.1K · p95 38us│ 未启用             │
├─────────────────────────────────────────────────────────────────────────┤
│ 正在测试 SEQ1M Q1T1 Write · 第 3/5 轮     00:24 / 约 01:10            │
│ [Diagnostics] [Copy] [Save]                          运行前 47 C → 49 C │
└─────────────────────────────────────────────────────────────────────────┘
```

线框中的数值只说明布局，不是预置或预期成绩。

### Toolbar

- `全部开始` 是唯一 primary command，带 play icon。
- 运行后同一位置和尺寸变成 `取消`，带 cancel icon；不同时显示 Start 与 Cancel。
- runs、size、target、unit、cache mode 使用 ComboBox。
- `读取 / 读写 / 读写+Mix` 使用 segmented control 或 ComboBox；空间不足时不要挤压 target selector。
- Mix 比例在高级设置或启用 Mix 后出现，使用有限选项 `R90/W10`、`R70/W30`、`R50/W50`，首版不需要任意文本输入。
- workload 运行中锁定所有会改变 plan 的控件；unit selector可以保持可用，因为它只改变显示。
- 高级设置使用 WPF-UI 已有 `Settings` symbol icon，不手绘 SVG。

### Target Strip

目标条是完整宽度的无嵌套信息 band，不做成 card in card。必须在点击开始前可见：

- device model、bus/protocol、volume、file system、system/removable role。
- free/total space、test file size、maximum planned writes。
- cache mode 的明确语义。
- 可用时显示起始温度；健康状态为 Critical 时禁止写测试，只允许用户切换到读取或更换目标。

warning 使用图标、文字和颜色共同表达。普通系统盘提示用 inline note，不弹阻塞式 MessageBox；真正会改变执行的风险，例如健康 Critical、空间不足、target changed，则禁用开始并给出原因。

### Result Matrix

- 左侧 workload label 是可点击命令，运行当前行的启用列。
- 每个 result cell 保持固定高度和最小宽度，数字变化不改变 grid tracks。
- 主数字使用 tabular numeral/等宽数字布局，字体不随 viewport 宽度缩放。
- 主单位默认 MB/s；4K cell 的第二行优先显示 IOPS 和 p95 latency。
- 顺序 cell 的第二行优先显示互补单位和 p95 latency。
- Mix cell 主值显示总吞吐，次级显示 read/write split 与 p95。
- 未运行显示 `--`；未启用显示 `未启用`；不支持显示原因入口，三者不能混淆。
- 当前 cell 使用底部或背景中的单色 accent progress rail，并显示 `第 3/5 轮`；不使用绿色渐变填充。
- 已完成 cell 保持结果，不因后续 cell 运行而清空。
- session 失败时保留已完成 cell，但顶部/底部明确标为 `未完整完成`，导出也保留 failure status。

### Footer

- 左侧显示阶段、当前 workload/sample 和总体进度。
- 中间显示 elapsed 与可用时的预计剩余时间；进度样本不足时只显示 elapsed，不伪造 ETA。
- 右侧显示 before/after temperature。
- `Diagnostics` 始终可打开当前运行日志；`Copy`/`Save` 在至少有一个完成结果后可用。
- 状态更新不能通过全局 `Mouse.OverrideCursor` 阻塞其他窗口。

## Narrow Window Behavior

小于约 `960 px` 时，参数区换成两行，matrix 保持固定列宽并使用水平滚动；不把四列压缩到文字重叠。

```text
┌──────────────────────────── 860 px ────────────────────────────┐
│ [全部开始] [5⌄] [1 GiB⌄] [C: Samsung PM9F1⌄]                 │
│ [设备模式⌄] [读+写⌄] [MB/s⌄] [设置]                          │
│ Samsung PM9F1 · C: · NTFS · 可用 518 GiB                      │
│ 文件 1 GiB · 最多写入 21 GiB                                 │
│ ┌ horizontal matrix viewport ───────────────────────────────┐ │
│ │ Workload | Read | Write | Mix ...                         │ │
│ └───────────────────────────────────────────────────────────┘ │
│ 当前阶段与 footer                                             │
└───────────────────────────────────────────────────────────────┘
```

- 次级指标可在非常窄的 cell 中减少为一行，但主值、单位和当前状态不能隐藏。
- 不将结果矩阵转换成一长串嵌套 cards；横向比较是核心任务。
- toolbar、target strip 和 footer 可纵向增长，不能覆盖 matrix。

## UI State Model

```text
Idle
  -> Preflight
  -> PreparingFile
  -> Running
  -> Cleanup
  -> Completed

Preflight / PreparingFile / Running
  -> CancelRequested
  -> Cleanup
  -> Canceled

Any active state
  -> Failed
  -> Cleanup
  -> FailedWithCleanupStatus
```

### Idle

- plan controls enabled。
- Start 是否 enabled 由 target validation 决定。
- 修改会影响 plan 的参数时，已有结果标为旧 session 并清空当前 matrix；Diagnostics 仍可查看上一份已完成报告直到新 session 开始。

### Preflight And Preparation

- matrix 不伪装成正在测量成绩。
- footer 分别显示 `正在验证目标`、`正在创建并初始化测试文件`。
- preparation progress 使用整体 progress，不填入某个 result cell。

### Running

- 只有当前 cell 显示 active progress。
- 已完成 cell 稳定保留。
- workload row buttons 和 plan controls disabled，unit/copy diagnostics 可按状态使用。

### Canceling And Cleanup

- 主按钮 disabled 并显示 `正在取消`，避免重复命令。
- close window 请求先进入 cancel/cleanup；清理完成后关闭。
- forced termination 后仍显示 `正在检查残留文件`。

### Completed

- footer 显示 elapsed、实际读写量、温度变化和质量摘要。
- 有高 variance、缓存或温度 flag 时显示非阻塞 warning。

### Failed

- 在 target/footer band 中显示用户可理解的 error、发生阶段和 cleanup 状态。
- 不用 MessageBox 展示完整 exception。
- `Retry` 只有 preflight 再验证通过后可用。

## Diagnostics And Export

Diagnostics 窗口采用 tab 或分区列表：

```text
Summary
  status / target / plan / elapsed / actual bytes / cleanup

Samples
  workload / operation / run / MB/s / IOPS / p50 / p95 / p99 / CV

Environment
  device / volume / extents / filesystem / alignment / cache flags
  OS / power / AC / before-after temperature and health

Protocol
  Core / worker / protocol versions / event sequence summary

Quality
  flags with human-readable reasons

Logs
  sanitized stderr and diagnostic path
```

Copy/Save 的稳定文本顺序：

```text
Run Status
Target Identity
Options And Workload Plan
Result Matrix
Raw Run Samples
Latency Percentiles
Read/Write Accounting
Environment And Temperature
Quality Flags
Cleanup
Versions And Timestamps
```

首版保存 `.txt` 和 `.json`。JSON 序列化 Core result，不序列化 WPF view model。默认报告隐藏完整序列号和完整临时路径，用户明确选择 diagnostics export 时才包含经过提示的详细环境信息。

## Accessibility And Theme

- 复用 `HwScopePanelBrush`、`HwScopeContentBrush`、`HwScopeCardBrush`、`HwScopeLineBrush`、`HwScopeTextBrush`、status brush 等动态资源。
- 控件圆角不超过 7 px，与现有页面一致。
- 不使用 gradient、装饰性背景或单一绿色主题。
- status、warning、active、completed 不只依赖颜色，必须有图标或文本。
- light/dark 下主数字、次级指标、disabled 和 progress 轨道满足可读对比度。
- keyboard tab 顺序按 Start -> options -> target -> row commands -> diagnostics/export。
- row buttons 提供完整 accessible name，例如 `运行 RND4K Q1T1 读取与写入测试`。
- progress 通过 automation live region 节流播报，不能每个 I/O request 都触发辅助技术通知。
- 长设备名使用 ellipsis + tooltip；预计写入量和错误原因允许换行。

## Concurrency And App Integration

- 同一应用只打开一个 `StorageBenchmarkWindow`。
- 同一 volume 同时只允许一个 HwScope storage worker，通过 volume-scoped mutex/lock 保证。
- 内存跑分可以并行打开，但 UI 应警告并行 benchmark 会污染结果；首版建议应用级 benchmark coordinator 阻止两种 benchmark 同时运行。
- storage health refresh 与温度快照是 best effort，不得与 timed I/O 并行执行。
- benchmark 期间 inventory change 或 device removal 必须通知 runner 重新核对 identity，不允许旧设备结果显示到新盘。
- 应用退出流程等待 cooperative cleanup 的有界时间，再执行 parent cleanup；不得无限卡住退出。

## Validation Strategy

### Unit Tests

- options、preset 和 workload plan 生成。
- maximum write budget checked arithmetic 和边界值。
- volume/path identity、same-volume 与 reparse point 校验。
- alignment 计算，4 KiB 不支持时不静默改 workload。
- progress JSON sequence、unknown event、protocol mismatch 和 truncated output。
- sample aggregate、median/CV、latency histogram quantiles。
- quality flag 判定。
- cleanup allowlist：错误目录、错误 GUID、reparse point、已有用户文件都拒绝删除。
- formatter/JSON 稳定输出与敏感字段脱敏。

### Worker Integration Tests

- 在专用临时 volume/directory 使用 64 MiB、1 run 完成四行 smoke。
- buffered/device mode 的 flags 与 result 一致。
- create-new 保证不覆盖 existing file。
- cancel during preparation、read、write、mix 和 cleanup。
- timeout/kill 后 parent cleanup。
- disk full、access denied、read-only、target removed、I/O error。
- malformed option 和 Core/worker protocol mismatch。
- write budget 达到上限时停止提交。

自动测试不能打开真实 `PhysicalDrive`，不能在开发者系统盘根目录创建任意文件。

### Hardware Matrix

| Target | Expected support | Key validation |
| --- | --- | --- |
| Direct NVMe system SSD / NTFS | Full | no-buffering alignment, system role, temp delta |
| Second NVMe SSD | Full | target identity and stable selection |
| Direct SATA SSD | Full | device cache note, SMART snapshot |
| Direct SATA HDD | Full but slow | no auto-run, QD random cancel, no unwanted polling |
| USB-SATA SSD/HDD | Best effort | removal, bridge identity, cleanup |
| USB-NVMe | Best effort | removal and temperature unavailable handling |
| exFAT removable SSD | Conditional | file-size limit and alignment |
| ReFS volume | Conditional | unbuffered behavior and file lifecycle |
| BitLocker system/data volume | Supported with flag | encryption overhead disclosure |
| Storage Spaces / RAID / dynamic | Deferred by default | multi-extent refusal |
| VHD/VHDX | Deferred/diagnostic only | virtual target labeling |
| Network share | Unsupported | preflight refusal |
| RAM disk | Unsupported | preflight refusal |

### Manual UI Checks

- default、最小和高 DPI 窗口无重叠。
- 125%、150%、200% scaling，长中英文设备名和无盘符 mount path。
- light/dark/Mica 切换不重建或丢失结果。
- 每个 active/error/unsupported/disabled 状态都可区分。
- 数字从 `--` 到最大位数不会改变行高/列宽。
- 横向 matrix 滚动不导致 footer 或 toolbar 消失。
- Cancel 和关闭窗口最终都能看到 cleanup 结论。
- Copy/Save 与 Diagnostics/result 一致。

## Rollout Stages

### Stage 1: Safe Engine Skeleton - Complete

- target/preflight/plan/result contracts。
- 独立 worker、协议版本、文件 create-new、budget、cancel 和 cleanup。
- 64 MiB internal smoke workload，不向用户宣称正式成绩。

### Stage 2: Read And Write Matrix - Complete

- 四行 Read/Write device-mode workload。
- raw samples、throughput、IOPS、latency、quality。
- GUI 参数区、target strip、matrix、Diagnostics、Copy/Save。

### Stage 3: Mix And Broader Targets - Partial

- Mix schedule、read/write split 和固定 Read -> Write -> Mix 顺序已完成。
- USB removal、exFAT/ReFS、BitLocker 和更多物理设备验证。
- CLI `benchmark storage` 已完成，并强制显式 `--drive`。

### Stage 4: Advanced Measurement

- durable/write-through 模式。
- sustained/steady-state 和显式 SLC cache 测试。
- 更完整的 background I/O、thermal 与 power telemetry。

## Acceptance Criteria For First User-Facing Release

- 用户在开始前能确认目标卷、物理设备、文件大小、缓存模式和最大写入量。
- worker 只创建新的 HwScope 临时文件，不存在任何 raw physical disk write path。
- 标准四行 Read/Write 能在受支持的单盘本地卷完成。
- MB/s、IOPS、p50/p95/p99、raw runs 和 CV 可在 result/Diagnostics 中查看。
- timeout、cancel、close、worker crash 和外接盘拔出都有有界退出与 cleanup 结论。
- 下次打开窗口能够发现并安全处理已验证的孤儿测试文件。
- buffered/device mode、flush timing 和 device cache 语义在主 UI 与导出中一致。
- target identity 在选择后、测试目录创建后和测试文件 handle 打开后核对，卷变化时不会把结果关联到错误物理盘。
- 高 variance、系统缓存、温度变化、电池供电和 incomplete cleanup 有明确 quality flag。
- light/dark、最小窗口、高 DPI 和长文本无重叠，matrix 尺寸稳定。
- Core unit tests、worker integration tests 和至少 NVMe/SATA SSD/HDD 的真实硬件验证完成。
- 文档和 README 只在功能真实落地并验证后声明支持。

## Deferred Decisions

后续阶段仍需通过原型或硬件验证确定：

- device mode 下 sample 之间的 flush/cooldown 默认值，以及是否会对不同控制器产生不可接受偏差。
- background I/O 的可靠检测来源和阈值。
- ReFS、4Kn、USB bridge 的最小支持矩阵。
- Diagnostics 是否需要导出完整 latency histogram，还是仅保存 buckets/quantiles。
- 是否允许用户为只读测试选择一个由 HwScope 上次运行留下并验证过的 fixture；首版默认每个 session 新建并清理。

这些决策不能改变已经确定的安全边界：文件级、create-new、有写入预算、身份复核、可取消、可清理、结果语义可见。
