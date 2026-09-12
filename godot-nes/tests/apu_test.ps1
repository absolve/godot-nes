# APU 测试：让测试 ROM 奏出**频率已知**的音，再分析导出的 PCM 核对。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests/apu_test.ps1
#
# 为什么这么测：音频没有"逐字节可比"的参照（各家模拟器的混音单位和滤波都不同），
# 所以改成核对**物理量**——开一个 440 Hz 的方波，输出里就该量到 440 Hz。
#
# 判据：
#   1. 静音 ROM 的输出必须是**精确的 0**（顺带证明混音没有直流偏移）；
#   2. 脉冲 / 三角波的频率要落在理论值 ±2% 内（数中值过零点的次数）；
#   3. 噪声没有单一频率，只要求"有声音"且过零点足够多（确实是宽带）。
#
# 理论频率（timer = 253，见 make_apu_test_rom.js）：
#   脉冲   = 1789773 / (16 * 254) ≈ 440.4 Hz
#   三角波 = 1789773 / (32 * 254) ≈ 220.2 Hz

[CmdletBinding()]
param(
    [int]$Frames = 180,
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

$ProjectDir = Split-Path -Parent $PSScriptRoot
$RepoRoot = Split-Path -Parent $ProjectDir
$WorkDir = Join-Path $RepoRoot '.ref'
$RomDir = Join-Path $PSScriptRoot 'roms'

$cases = @(
    @{ Name = 'apu-silent.nes';   Expect = 'silent';   Note = '什么都不开' },
    @{ Name = 'apu-pulse.nes';    Expect = 'pulse';    Note = '脉冲 1，440.4 Hz' },
    @{ Name = 'apu-triangle.nes'; Expect = 'triangle'; Note = '三角波，220.2 Hz' },
    @{ Name = 'apu-noise.nes';    Expect = 'noise';    Note = '噪声（宽带）' },
    @{ Name = 'apu-dmc-off.nes';  Expect = 'dmc-off';  Note = 'DMC 关掉之后必须安静'; Tail = 0.5 }
)

foreach ($case in $cases) {
    if (-not (Test-Path (Join-Path $RomDir $case.Name))) {
        Write-Host "测试 ROM 不存在，先生成…"
        & node (Join-Path $PSScriptRoot 'make_apu_test_rom.js')
        break
    }
}

if (-not (Test-Path $GodotExe)) {
    Write-Host "找不到：$GodotExe" -ForegroundColor Red
    exit 2
}

if (-not (Test-Path $WorkDir)) {
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
}

$failures = 0

foreach ($case in $cases) {
    $romPath = Join-Path $RomDir $case.Name
    $pcmPath = Join-Path $WorkDir ($case.Name -replace '\.nes$', '.pcm')

    Write-Host ""
    Write-Host "==== $($case.Name)  ($($case.Note)) ===="

    $ErrorActionPreference = 'Continue'
    & $GodotExe --headless --path $ProjectDir -- --rom $romPath `
        --dump-audio $pcmPath --dump-frame-after $Frames 2>&1 | Out-Null
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'

    if ($exitCode -ne 0 -or -not (Test-Path $pcmPath)) {
        Write-Host "  [FAIL] 导出音频失败（退出码 $exitCode）" -ForegroundColor Red
        $failures++
        continue
    }

    # ---- 读 PCM 并统计 ----
    $bytes = [System.IO.File]::ReadAllBytes($pcmPath)
    $count = [int]($bytes.Length / 2)
    if ($count -lt 10000) {
        Write-Host "  [FAIL] 样本太少：$count" -ForegroundColor Red
        $failures++
        continue
    }

    $values = New-Object 'int[]' $count
    $min = 32767
    $max = -32768
    for ($i = 0; $i -lt $count; $i++) {
        $v = [BitConverter]::ToInt16($bytes, $i * 2)
        $values[$i] = $v
        if ($v -lt $min) { $min = $v }
        if ($v -gt $max) { $max = $v }
    }

    # 尾段统计（"关掉之后必须安静"这类判据要用）
    $tailPeak = 0
    if ($case.ContainsKey('Tail')) {
        $tailSamples = [int]($case.Tail * 44100)
        $tailStart = [Math]::Max(0, $count - $tailSamples)
        for ($i = $tailStart; $i -lt $count; $i++) {
            $abs = [Math]::Abs($values[$i])
            if ($abs -gt $tailPeak) { $tailPeak = $abs }
        }
    }
    # 开头峰值（前半段里最大的一小段）—— 用来确认"前面确实有声音"
    $headPeak = 0
    $headEnd = [int]($count / 3)
    for ($i = 0; $i -lt $headEnd; $i++) {
        $abs = [Math]::Abs($values[$i])
        if ($abs -gt $headPeak) { $headPeak = $abs }
    }

    # 输出是单极性的（0 到某个正值），所以用中值当阈值数过零点，不能拿 0 当阈值
    $mid = ($min + $max) / 2.0
    $crossings = 0
    $previousAbove = $null
    for ($i = 0; $i -lt $count; $i++) {
        $above = $values[$i] -gt $mid
        if ($null -ne $previousAbove -and $above -ne $previousAbove) { $crossings++ }
        $previousAbove = $above
    }

    $duration = $count / 44100.0
    $frequency = $crossings / 2.0 / $duration
    $peak = [Math]::Max([Math]::Abs($min), [Math]::Abs($max))

    Write-Host ("  样本 {0}，时长 {1:N2}s，峰值 {2}，过零 {3}，频率 {4:N1} Hz" -f `
        $count, $duration, $peak, $crossings, $frequency)

    # ---- 判据 ----
    switch ($case.Expect) {
        'silent' {
            if ($min -eq 0 -and $max -eq 0) {
                Write-Host "  [OK]   静音 ROM 输出精确为 0（混音没有直流偏移）" -ForegroundColor Green
            } else {
                Write-Host "  [FAIL] 静音 ROM 竟然有输出：min=$min max=$max" -ForegroundColor Red
                $failures++
            }
        }
        'pulse' {
            if ($peak -lt 1000) {
                Write-Host "  [FAIL] 脉冲通道没声音（峰值 $peak）" -ForegroundColor Red
                $failures++
            } elseif ([Math]::Abs($frequency - 440.4) / 440.4 -gt 0.02) {
                Write-Host "  [FAIL] 频率不对：量到 $([Math]::Round($frequency,1)) Hz，期望 440.4 Hz" -ForegroundColor Red
                $failures++
            } else {
                Write-Host "  [OK]   脉冲频率 $([Math]::Round($frequency,1)) Hz，与理论 440.4 Hz 相符" -ForegroundColor Green
            }
        }
        'triangle' {
            if ($peak -lt 1000) {
                Write-Host "  [FAIL] 三角波通道没声音（峰值 $peak）" -ForegroundColor Red
                $failures++
            } elseif ([Math]::Abs($frequency - 220.2) / 220.2 -gt 0.02) {
                Write-Host "  [FAIL] 频率不对：量到 $([Math]::Round($frequency,1)) Hz，期望 220.2 Hz" -ForegroundColor Red
                $failures++
            } else {
                Write-Host "  [OK]   三角波频率 $([Math]::Round($frequency,1)) Hz，与理论 220.2 Hz 相符" -ForegroundColor Green
            }
        }
        'noise' {
            if ($peak -lt 1000) {
                Write-Host "  [FAIL] 噪声通道没声音（峰值 $peak）" -ForegroundColor Red
                $failures++
            } elseif ($crossings -lt 2000) {
                Write-Host "  [FAIL] 噪声过零点太少（$crossings），不像宽带信号" -ForegroundColor Red
                $failures++
            } else {
                Write-Host "  [OK]   噪声有声音且是宽带的（过零 $crossings 次）" -ForegroundColor Green
            }
        }
        'dmc-off' {
            # 关掉 DMC 之后，移位器必须停住、DAC 保持电平 —— 输出里不能再有任何抖动。
            # 少了这个判断时，最后那个字节会被反复移位，DAC 一直抖，尾段峰值跑不掉。
            Write-Host ("  开头峰值 {0}，尾段（最后 {1:N1} 秒）峰值 {2}" -f `
                $headPeak, $case.Tail, $tailPeak)
            if ($headPeak -lt 500) {
                Write-Host "  [FAIL] 前面就没声音，DMC 根本没播起来（峰值 $headPeak）" -ForegroundColor Red
                $failures++
            } elseif ($tailPeak -gt 20) {
                Write-Host "  [FAIL] 关掉 DMC 之后还有持续抖动（尾段峰值 $tailPeak）—— 移位器没停" -ForegroundColor Red
                $failures++
            } else {
                Write-Host "  [OK]   DMC 播过（开头峰值 $headPeak），关掉之后彻底安静（尾段峰值 $tailPeak）" -ForegroundColor Green
            }
        }
    }
}

Write-Host ""
if ($failures -gt 0) {
    Write-Host "APU 测试失败：$failures 项" -ForegroundColor Red
    exit 1
}

Write-Host "APU 测试通过：静音为 0、脉冲/三角波频率符合理论值、噪声是宽带信号。" -ForegroundColor Green
exit 0
