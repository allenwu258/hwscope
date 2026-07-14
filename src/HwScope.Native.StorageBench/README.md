# HwScope Native Storage Benchmark

`storagebench.exe` is the isolated file-backed worker used by the HwScope storage benchmark runner.

Build on Windows x64:

```powershell
.\scripts\build-msvc.ps1
```

The worker only accepts a Core-generated test file path and always opens it with `CREATE_NEW`. It does not expose a physical-drive or raw-device mode.
