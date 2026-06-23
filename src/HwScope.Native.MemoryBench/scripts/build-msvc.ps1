$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent $PSScriptRoot
$build = Join-Path $repo "build"
$vsdev = "C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\Tools\VsDevCmd.bat"
$bundledCmake = "C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"

if (Test-Path $bundledCmake) {
    $cmake = $bundledCmake
} else {
    $cmake = "cmake"
}

if (Test-Path $vsdev) {
    & cmd /c "`"$vsdev`" -arch=x64 -host_arch=x64 >nul && `"$cmake`" -S `"$repo`" -B `"$build`" -G `"Visual Studio 17 2022`" -A x64 && `"$cmake`" --build `"$build`" --config Release"
} else {
    & $cmake -S $repo -B $build -G "Visual Studio 17 2022" -A x64
    & $cmake --build $build --config Release
}

Write-Host ""
Write-Host "Built: $build\Release\membench.exe"
