# HwScope.Native.Spd

Native C++ worker for HwScope Memory / SPD collection.

This project provides the offline SPD parser and JSON protocol consumed by `HwScope.Core.Hardware.Memory.NativeSpdProcessProvider`. Hardware SPD acquisition is currently not implemented because a safe general Windows implementation requires a kernel driver. The driver-dependent reader work is parked; fixture/offline parsing remains active.

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

Current default output:

```json
{
  "schemaVersion": 1,
  "workerVersion": "0.2.0",
  "backend": "none",
  "status": "notImplemented",
  "modules": [],
  "diagnostics": [
    "SPD hardware acquisition is not implemented.",
    "Offline SPD parsing remains available through the fixture backend."
  ]
}
```

Future worker versions should keep `schemaVersion` stable until the JSON shape changes. Module timing fields such as `casLatency`, `trcd`, `trp`, `tras`, and `trc` may be emitted as JSON numbers or strings; Core accepts both.

Fixture validation command:

```powershell
.\build\Release\spd.exe --json --backend fixture --fixture .\fixtures\ddr5-sodimm-32gb.sample.json
.\build\Release\spd.exe --json --backend fixture --fixture .\fixtures\ddr4-udimm-32gb.raw.sample.json
.\build\Release\spd.exe --json --backend fixture --fixture .\fixtures\ddr4-udimm-32gb.bad-crc.sample.json
.\build\Release\spd.exe --json --backend fixture --fixture .\fixtures\unknown-spd-type.sample.json
.\build\Release\spd.exe --json --backend fixture --fixture .\fixtures\invalid-hex.sample.json
```

The fixture backend supports two development formats:

- Worker-payload-shaped JSON, kept for UI/Core integration fixtures.
- Raw `bytesHex` JSON, parsed by the native SPD parser before emitting the worker payload. The DDR4 first-pass parser emits identity, organization, voltage, JEDEC timing, CRC status and SHA-256 raw metadata.

To make the WPF page show fixture-backed SPD fields, set `HWSCOPE_SPD_FIXTURE` to an absolute fixture path before launching the app.

Fixture selection for development:

```powershell
$env:HWSCOPE_SPD_FIXTURE = "C:\path\to\ddr4-udimm-32gb.raw.sample.json"
```

## Current Scope

Active work:

- Fixture/offline SPD byte parsing.
- DDR4/DDR5 field decoding, CRC/checksum validation and profile extraction.
- Core/UI integration through schema-versioned JSON.

Parked work:

- Windows SMBus controller probing and chipset adapters.
- Real SPD EEPROM / DDR5 SPD Hub acquisition.
- Kernel driver packaging, signing and hardware compatibility testing.

The default `auto` backend returns `notImplemented`. Hardware acquisition will only resume after a separately reviewed, signed and constrained driver plan exists.

See [`../../docs/memory-spd-detail-implementation-plan.md`](../../docs/memory-spd-detail-implementation-plan.md) for the full Stage 3A/3B development plan.
