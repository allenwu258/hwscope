# HwScope Native Storage Benchmark

`storagebench.exe` is the isolated Windows file-backed worker used by the HwScope storage benchmark runner.

Build on Windows x64:

```powershell
.\scripts\build-msvc.ps1
```

In the supported integration, Core passes a GUID test-file path, session ID, expected volume GUID, fixed byte plan, and write budget. The worker creates the path with `CREATE_NEW`, validates the opened handle against that volume, emits the volume serial and 128-bit file ID, and then reopens only the same file with `OPEN_EXISTING` for benchmark I/O. It does not expose a physical-drive or raw-device mode.

Read, Write, and optional R70/W30 Mix workloads use overlapped I/O with an IO completion port. A 64 MiB aligned deterministic random pool supplies write buffers without generating or copying full blocks inside the timed region. Multi-row runs execute every Read workload first, then every Write workload, then every Mix workload.

Protocol version 1 uses newline-delimited JSON on stdout. A successful run emits `file_created`, one final `result`, and `completed`; cancellation is requested through stdin and cleanup is attempted before exit.
