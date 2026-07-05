# HwScope.Native.Spd

Native C++ worker for HwScope Memory / SPD collection.

This project is the Stage 3 worker scaffold. It already provides the process boundary and JSON protocol consumed by `HwScope.Core.Hardware.Memory.NativeSpdProcessProvider`. The default backend reports raw SPD access as blocked unless a safe privileged SMBus/SPD backend is implemented for the current platform.

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
  "status": "platformBlocked",
  "modules": [],
  "diagnostics": [
    "Windows does not expose a stable user-mode API for raw SPD EEPROM reads on this platform.",
    "A privileged SMBus/SPD backend or supported controller-specific reader is required for raw hardware access.",
    "Set HWSCOPE_SPD_FIXTURE to a fixture JSON path to validate parser/UI integration."
  ]
}
```

Future worker versions should keep `schemaVersion` stable until the JSON shape changes. Module timing fields such as `casLatency`, `trcd`, `trp`, `tras`, and `trc` may be emitted as JSON numbers or strings; Core accepts both.

Fixture validation command:

```powershell
.\build\Release\spd.exe --json --backend fixture --fixture .\fixtures\ddr5-sodimm-32gb.sample.json
```

The fixture backend currently passes through worker-payload-shaped JSON. It exists to validate Core/UI integration with parsed SPD fields before unsafe raw SMBus access is available. To make the WPF page show fixture-backed SPD fields, set `HWSCOPE_SPD_FIXTURE` to an absolute fixture path before launching the app.

## Real Reader Roadmap

Real SPD support is split into two tracks:

- A fixture/offline SPD bytes parser that can parse DDR4/DDR5 samples without hardware access.
- A Windows raw reader backend that attempts SMBus/SPD EEPROM access only when a supported, safe path is available.

The parser should land first. It should add `--backend fixture --fixture <path>`, checksum/CRC validation, DDR4/DDR5 detection, identity fields, organization fields, voltages, and JEDEC timing profiles. The default `--backend auto` can keep returning `notImplemented` until a Windows reader backend is safe enough to enable.

See [`../../docs/memory-spd-detail-implementation-plan.md`](../../docs/memory-spd-detail-implementation-plan.md) for the full Stage 3A/3B development plan.
