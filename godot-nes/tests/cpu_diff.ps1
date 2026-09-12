# CPU 差分测试：用 fogleman/nes（Go）当参照，对同一个 ROM 逐行对比 CPU 状态。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests/cpu_diff.ps1
#
# 原理：两边都按同一种格式打印「每条指令执行前」的 PC/寄存器/标志位/周期数，
#       然后逐行比字符串。只要有一处寄存器、一处标志位或者一个周期数对不上，就会报出来。
#
# 参照实现的准备方法见 tests/README.md（它不随仓库分发）。

[CmdletBinding()]
param(
    [string]$Rom,
    [int]$Count = 800,
    [string]$GodotExe = $env:GODOT
)
# ---- 先确认 Godot 控制台程序在哪儿（没设的话给一句人话，而不是 PowerShell 的报错）----
if ([string]::IsNullOrWhiteSpace($GodotExe) -or -not (Test-Path $GodotExe)) {
    Write-Host "[FAIL] 没找到 Godot（.NET 版）的控制台程序。" -ForegroundColor Red
    Write-Host "       两种指定方式，任选一种：" -ForegroundColor Yellow
    Write-Host "         1) 设环境变量：`$env:GODOT = '<Godot 目录>\Godot_v4.7.2-stable_mono_win64_console.exe'" -ForegroundColor Yellow
    Write-Host "         2) 传给脚本：  -GodotExe '<Godot 目录>\Godot_v4.7.2-stable_mono_win64_console.exe'" -ForegroundColor Yellow
    exit 1
}

$ErrorActionPreference = 'Stop'

$ProjectDir = Split-Path -Parent $PSScriptRoot          # …/godot-nes/godot-nes
$RepoRoot = Split-Path -Parent $ProjectDir              # …/godot-nes
$WorkDir = Join-Path $RepoRoot '.ref'
$Oracle = Join-Path $WorkDir 'nesoracle\nesoracle.exe'
$RefTrace = Join-Path $WorkDir 'trace-ref.txt'
$MyTrace = Join-Path $WorkDir 'trace-mine.txt'

if ([string]::IsNullOrEmpty($Rom)) {
    $Rom = Join-Path $PSScriptRoot 'roms\torture.nes'
}

foreach ($path in @($Rom, $Oracle, $GodotExe)) {
    if (-not (Test-Path $path)) {
        Write-Host "找不到：$path" -ForegroundColor Red
        Write-Host "参照实现和测试 ROM 的准备方法见 tests/README.md" -ForegroundColor Yellow
        exit 2
    }
}

if (-not (Test-Path $WorkDir)) {
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
}

Write-Host "参照实现：$Oracle"
Write-Host "测试 ROM：$Rom"
Write-Host "对比条数：$Count"

# Go 的缓存/代理都指到仓库内部，避免写用户目录、也避免联网
$env:GOCACHE = Join-Path $WorkDir 'gocache'
$env:GOPATH = Join-Path $WorkDir 'gopath'
$env:GOPROXY = 'off'
$env:GOTOOLCHAIN = 'local'
$env:GOTELEMETRY = 'off'

# 原生程序（Go、Godot）会往 stderr 打印无害的警告（比如沙箱里写不了 user://），
# 在 $ErrorActionPreference='Stop' 下会被当成终止错误，所以这两次调用期间放宽。
$ErrorActionPreference = 'Continue'

# 1. 参照实现跑一遍
& $Oracle $Rom $Count $RefTrace 2>&1 | Out-Null
$oracleExit = $LASTEXITCODE

# 2. godot-nes 跑一遍
& $GodotExe --headless --path $ProjectDir -- --rom $Rom --trace-cpu $Count --trace-out $MyTrace 2>&1 |
    Select-String -Pattern 'CPU trace|非法指令' | ForEach-Object { $_.Line }
$godotExit = $LASTEXITCODE

$ErrorActionPreference = 'Stop'

if ($oracleExit -ne 0) {
    Write-Host "参照实现执行失败（退出码 $oracleExit）" -ForegroundColor Red
    exit 2
}

if ($godotExit -ne 0) {
    Write-Host "godot-nes 执行失败（退出码 $godotExit）" -ForegroundColor Red
    exit 2
}

# 3. 逐行比
$ref = Get-Content $RefTrace
$mine = Get-Content $MyTrace
Write-Host ""
Write-Host "参照 $($ref.Count) 行 / godot-nes $($mine.Count) 行"

$shared = [Math]::Min($ref.Count, $mine.Count)
$firstDiff = -1
for ($i = 0; $i -lt $shared; $i++) {
    if ($ref[$i] -ne $mine[$i]) {
        $firstDiff = $i
        break
    }
}

if ($firstDiff -ge 0) {
    Write-Host "第 $($firstDiff + 1) 行开始不一致：" -ForegroundColor Red
    $from = [Math]::Max(0, $firstDiff - 2)
    $to = [Math]::Min($shared - 1, $firstDiff + 2)
    for ($i = $from; $i -le $to; $i++) {
        $mark = if ($i -eq $firstDiff) { '  <== 分歧' } else { '' }
        Write-Host ("  参照      [{0,4}] {1}{2}" -f ($i + 1), $ref[$i], $mark)
        Write-Host ("  godot-nes [{0,4}] {1}{2}" -f ($i + 1), $mine[$i], $mark)
    }

    exit 1
}

if ($ref.Count -ne $mine.Count) {
    # 前面全都一样，只是一边提前停了（通常是撞到非法指令，参照实现会原地打转）
    Write-Host "前 $shared 行完全一致，但行数不同（参照 $($ref.Count) / 这边 $($mine.Count)）" -ForegroundColor Yellow
    Write-Host "godot-nes 末尾：" -ForegroundColor Yellow
    $mine | Select-Object -Last 3 | ForEach-Object { Write-Host "  $_" }
    exit 1
}

Write-Host "前 $shared 行完全一致：寄存器、标志位、周期数全部对得上。" -ForegroundColor Green
exit 0
