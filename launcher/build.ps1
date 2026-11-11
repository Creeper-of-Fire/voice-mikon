# 构建 VoiceMikon.exe
#
# 为什么不用 vcvarsall.bat:cargo 不认 vcvars —— cargo 找不到 link.exe 就直接
# 报 "linker `link.exe` not found"。所以这个脚本自己把 MSVC / Windows SDK 的
# PATH、INCLUDE、LIB 设好(规格 §4.3)。
#
# 用法:
#   pwsh -File .\build.ps1
#   pwsh -File .\build.ps1 -CargoExtra '--offline'

[CmdletBinding()]
param(
    [string]$CargoExtra = ''
)

$ErrorActionPreference = 'Stop'

$launcherDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDir  = Split-Path -Parent $launcherDir
$runtimeBin  = Join-Path $projectDir 'runtime\bin'

# ---------------------------------------------------------------- MSVC
$vcRoot = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC'
if (-not (Test-Path $vcRoot)) {
    throw "找不到 MSVC 工具链目录: $vcRoot"
}
$msvc = Get-ChildItem $vcRoot -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'bin\Hostx64\x64\link.exe') } |
    Sort-Object { [version]($_.Name -replace '^(\d+\.\d+\.\d+).*$', '$1') } -Descending |
    Select-Object -First 1
if (-not $msvc) {
    throw "找不到带 link.exe 的 MSVC 版本(在 $vcRoot 下)"
}
$msvc = $msvc.FullName
Write-Host "MSVC      : $msvc"

# ---------------------------------------------------------------- Windows SDK
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
if (-not (Test-Path $sdkRoot)) {
    throw "找不到 Windows Kits: $sdkRoot"
}
$sdkVer = Get-ChildItem (Join-Path $sdkRoot 'Include') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '10.*' } |
    Sort-Object { [version]($_.Name -replace '^(\d+\.\d+\.\d+).*$', '$1') } -Descending |
    Select-Object -First 1
if (-not $sdkVer) {
    throw "在 $sdkRoot\Include 下找不到 10.* 的 SDK 版本"
}
$sdkVer = $sdkVer.Name
Write-Host "SDK       : $sdkVer"

# ---------------------------------------------------------------- 存在性检查
$need = @(
    (Join-Path $msvc 'bin\Hostx64\x64\link.exe'),
    (Join-Path $msvc 'include'),
    (Join-Path $msvc 'lib\x64'),
    (Join-Path $sdkRoot "Include\$sdkVer\ucrt"),
    (Join-Path $sdkRoot "Include\$sdkVer\um"),
    (Join-Path $sdkRoot "Include\$sdkVer\shared"),
    (Join-Path $sdkRoot "Lib\$sdkVer\ucrt\x64"),
    (Join-Path $sdkRoot "Lib\$sdkVer\um\x64")
)
foreach ($p in $need) {
    if (-not (Test-Path $p)) {
        throw "构建需要的路径不存在: $p"
    }
}
# 抽查两个关键 lib
foreach ($lib in @('kernel32.lib', 'user32.lib')) {
    $hit = Get-ChildItem (Join-Path $sdkRoot "Lib\$sdkVer\um\x64") -Filter $lib -ErrorAction SilentlyContinue
    if (-not $hit) { throw "找不到 $lib(在 $sdkRoot\Lib\$sdkVer\um\x64)" }
}

# ---------------------------------------------------------------- 设环境
$env:PATH    = "$msvc\bin\Hostx64\x64;$sdkRoot\bin\$sdkVer\x64;$env:PATH"
$env:INCLUDE = "$msvc\include;$sdkRoot\Include\$sdkVer\ucrt;$sdkRoot\Include\$sdkVer\um;$sdkRoot\Include\$sdkVer\shared;$sdkRoot\Include\$sdkVer\winrt"
$env:LIB     = "$msvc\lib\x64;$sdkRoot\Lib\$sdkVer\ucrt\x64;$sdkRoot\Lib\$sdkVer\um\x64"

# ---------------------------------------------------------------- 构建
Push-Location $launcherDir
try {
    Write-Host "cargo build --release ..."
    if ($CargoExtra) {
        & cargo build --release $CargoExtra.Split(' ') 2>&1 | Write-Host
    } else {
        & cargo build --release 2>&1 | Write-Host
    }
    if ($LASTEXITCODE -ne 0) {
        throw "cargo build 失败(exit $LASTEXITCODE)"
    }

    $built = Join-Path $launcherDir 'target\release\VoiceMikon.exe'
    if (-not (Test-Path $built)) {
        throw "构建产物不存在: $built"
    }

    New-Item -ItemType Directory -Force -Path $runtimeBin | Out-Null
    $target = Join-Path $runtimeBin 'VoiceMikon.exe'
    Copy-Item -Force $built $target
    Write-Host ""
    Write-Host "构建完成:"
    Write-Host "  $built"
    Write-Host "  -> $target  ($((Get-Item $target).Length) 字节)"
}
finally {
    Pop-Location
}
