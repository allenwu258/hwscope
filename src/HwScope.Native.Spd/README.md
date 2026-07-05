# HwScope.Native.Spd

Native C++ worker for HwScope Memory / SPD collection.

This project is the Stage 3 worker scaffold. It already provides the process boundary and JSON protocol consumed by `HwScope.Core.Hardware.Memory.NativeSpdProcessProvider`, but it does not yet read raw SMBus/SPD EEPROM bytes. Until that low-level reader is implemented, the worker returns a non-fatal `notImplemented` status with an empty module list.

## Build

From this directory:

```powershell
.\scripts\build-msvc.ps1
```

The output is:

```text
build\Release\spd.exe
```

After the native Release artifact exists, `HwScope.App` and `HwScope.Cli` copy it to their output directories during `dotnet build`:

```text
bin\Debug\net8.0-windows\native\spd.exe
```

The C# provider also keeps a source-tree `build\Release` developer fallback so App/CLI can run directly after a manual native build.

## Invocation

```powershell
.\build\Release\spd.exe --json
```

Current scaffold output:

```json
{
  "schemaVersion": 1,
  "workerVersion": "0.1.0",
  "status": "notImplemented",
  "modules": [],
  "diagnostics": [
    "Native SPD worker scaffold is available, but raw SMBus/SPD reading is not implemented yet.",
    "HwScope will continue to show WMI/SMBIOS-backed memory fields and SPD placeholders."
  ]
}
```

Future worker versions should keep `schemaVersion` stable until the JSON shape changes. Module timing fields such as `casLatency`, `trcd`, `trp`, `tras`, and `trc` may be emitted as JSON numbers or strings; Core accepts both.

## Real Reader Roadmap

Real SPD support is split into two tracks:

- A fixture/offline SPD bytes parser that can parse DDR4/DDR5 samples without hardware access.
- A Windows raw reader backend that attempts SMBus/SPD EEPROM access only when a supported, safe path is available.

The parser should land first. It should add `--backend fixture --fixture <path>`, checksum/CRC validation, DDR4/DDR5 detection, identity fields, organization fields, voltages, and JEDEC timing profiles. The default `--backend auto` can keep returning `notImplemented` until a Windows reader backend is safe enough to enable.

See [`../../docs/memory-spd-detail-implementation-plan.md`](../../docs/memory-spd-detail-implementation-plan.md) for the full Stage 3A/3B development plan.
