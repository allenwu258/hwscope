# HwScope.Native.Spd

Native C++ worker for HwScope Memory / SPD collection.

This project is the Stage 3 worker scaffold. It already provides the process boundary and JSON protocol consumed by `HwScope.Core.Hardware.Memory.NativeSpdProcessProvider`, but it does not yet read raw SMBus/SPD EEPROM bytes. Until that low-level reader is implemented, the worker returns a non-fatal `platformBlocked` status with an empty module list.

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
    "Native SPD worker scaffold is available, but raw SMBus/SPD reading is not implemented yet.",
    "HwScope will continue to show WMI/SMBIOS-backed memory fields and SPD placeholders."
  ]
}
```

Future worker versions should keep `schemaVersion` stable until the JSON shape changes. Module timing fields such as `casLatency`, `trcd`, `trp`, `tras`, and `trc` may be emitted as JSON numbers or strings; Core accepts both.
