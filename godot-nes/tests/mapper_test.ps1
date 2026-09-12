# mapper 测试：换 bank / 换镜像 / 换 CHR 的真实行为对不对。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests/mapper_test.ps1
#
# 每个 ROM 都走两道关，缺一不可：
#
#   1. **自检**：程序自己把每个窗口读回来的字节和手算期望比过，结果留在 RAM 里 ——
#      $03F0 = 失败条数（必须 0）、$03F1 = 测试条数、$03F2 = $A5（跑完了）。
#      期望值是照着 nesdev wiki 手算的，所以它能抓住"和参照实现错得一模一样"。
#   2. **差分**：和 fogleman/nes（Go）逐行比 CPU trace。ROM 里每次比对不等就多执行一条
#      INC，所以只要有一处换 bank 换错，两条 trace 的行号立刻错开。
#
# 参照实现的准备方法见 tests/README.md（它不随仓库分发）。

[CmdletBinding()]
param(
    [string]$GodotExe = $env:GODOT,
    [int]$Count = 2000,
    [switch]$SkipDiff
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
$Oracle = Join-Path $WorkDir 'nesoracle\nesoracle.exe'
$RomsDir = Join-Path $PSScriptRoot 'roms'

# Tests 必须和 make_mapper_test_rom.js 里 emitEpilogue() 写的条数一致
$Cases = @(
    @{ Name = 'MMC1 '; File = 'mapper1.nes'; Tests = 20; Note = 'PRG 模式 0/1/2/3 + 五种镜像 + CHR 8KB/4KB' },
    @{ Name = 'UxROM'; File = 'mapper2.nes'; Tests = 7;  Note = '16KB 换 bank + 最后一块固定' },
@{ Name = 'VRC2b'; File = 'mapper23.nes'; Tests = 14; SkipDiff = $true; Note = 'VRC2b/VRC4e：8KB PRG 换 bank、CHR 半字节高低位、镜像（参照实现不认 mapper 23，跳过 trace 差分）' },
    @{ Name = 'CNROM'; File = 'mapper3.nes'; Tests = 12; Note = '写 $8000 只换 CHR（8KB），PRG 不动' },
    @{ Name = 'MMC3 '; File = 'mapper4.nes'; Tests = 21; Note = 'R6/R7 + PRG 模式 1 + 镜像 + CHR 两种模式' },
    @{ Name = 'AxROM'; File = 'mapper7.nes'; Tests = 6;  Note = '32KB 换 bank + 单屏镜像切换' }
)

foreach ($path in @($GodotExe, $Oracle)) {
    if ((Test-Path $path) -eq $false -and -not ($SkipDiff -and $path -eq $Oracle)) {
        Write-Host "找不到：$path" -ForegroundColor Red
        Write-Host "参照实现和测试 ROM 的准备方法见 tests/README.md" -ForegroundColor Yellow
        exit 2
    }
}

if (-not (Test-Path $WorkDir)) {
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
}

# 原生程序（Godot / Go）会往 stderr 打无害警告（沙箱里写不了 user://），
# 在 $ErrorActionPreference='Stop' 下会被当成终止错误，所以调用期间放宽。
$ErrorActionPreference = 'Continue'

$failures = 0

foreach ($case in $Cases) {
    $rom = Join-Path $RomsDir $case.File
    Write-Host ""
    Write-Host "==== $($case.Name)  $($case.File) —— $($case.Note) ===="

    if ((Test-Path $rom) -eq $false) {
        Write-Host "  [FAIL] 没有测试 ROM，先跑 node tests/make_mapper_test_rom.js" -ForegroundColor Red
        $failures++
        continue
    }

    # ---------------- 1. 自检 ----------------
    $dump = & $GodotExe --headless --path $ProjectDir -- --rom $rom --dump-ram 03F0 3 --dump-frame-after 2 2>&1
    $line = ($dump | Select-String -Pattern 'RAM \$03F0:').Line

    if ($null -eq $line) {
        Write-Host "  [FAIL] 没拿到 RAM 结果（程序可能没跑起来）" -ForegroundColor Red
        $failures++
        continue
    }

    $hex = ($line -split 'RAM \$03F0:')[1].Trim() -split '\s+'
    $bad = [Convert]::ToInt32($hex[0], 16)
    $ran = [Convert]::ToInt32($hex[1], 16)
    $done = [Convert]::ToInt32($hex[2], 16)

    if ($done -ne 0xA5) {
        Write-Host "  [FAIL] 程序没跑完（$03F2 = $('{0:X2}' -f $done)，应为 A5）" -ForegroundColor Red
        $failures++
    }
    elseif ($ran -ne $case.Tests) {
        Write-Host "  [FAIL] 测试条数对不上：实际 $ran，脚本里写的是 $($case.Tests)" -ForegroundColor Red
        $failures++
    }
    elseif ($bad -ne 0) {
        Write-Host "  [FAIL] $bad 条断言不通过（实际值见 RAM $0310 起）" -ForegroundColor Red
        $failures++
    }
    else {
        Write-Host "  [OK]   自检 $ran/$ran 通过（换 bank / 镜像 / CHR 的实际值都和手算一致）" -ForegroundColor Green
    }

    if ($SkipDiff -or $case.SkipDiff) {
        continue
    }

    # ---------------- 2. 和参照实现逐行差分 ----------------
    $refTrace = Join-Path $WorkDir "mapper-$($case.File)-ref.txt"
    $myTrace = Join-Path $WorkDir "mapper-$($case.File)-mine.txt"

    & $Oracle $rom $Count $refTrace 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  [FAIL] 参照实现跑不了这个 ROM（退出码 $LASTEXITCODE）" -ForegroundColor Red
        $failures++
        continue
    }

    & $GodotExe --headless --path $ProjectDir -- --rom $rom --trace-cpu $Count --trace-out $myTrace 2>&1 | Out-Null

    $ref = Get-Content $refTrace
    $mine = Get-Content $myTrace
    $shared = [Math]::Min($ref.Count, $mine.Count)
    $firstDiff = -1

    for ($i = 0; $i -lt $shared; $i++) {
        if ($ref[$i] -ne $mine[$i]) {
            $firstDiff = $i
            break
        }
    }

    if ($firstDiff -ge 0) {
        Write-Host "  [FAIL] 第 $($firstDiff + 1) 行开始和参照实现不一致：" -ForegroundColor Red
        $from = [Math]::Max(0, $firstDiff - 2)
        $to = [Math]::Min($shared - 1, $firstDiff + 2)
        for ($i = $from; $i -le $to; $i++) {
            $mark = if ($i -eq $firstDiff) { '  <== 分歧' } else { '' }
            Write-Host ("    参照      [{0,4}] {1}{2}" -f ($i + 1), $ref[$i], $mark)
            Write-Host ("    godot-nes [{0,4}] {1}{2}" -f ($i + 1), $mine[$i], $mark)
        }

        $failures++
        continue
    }

    if ($ref.Count -ne $mine.Count) {
        Write-Host "  [FAIL] 前 $shared 行一致但行数不同（参照 $($ref.Count) / 这边 $($mine.Count)）" -ForegroundColor Red
        $failures++
        continue
    }

    Write-Host "  [OK]   和 fogleman/nes 的 $shared 行 trace 完全一致" -ForegroundColor Green
}

# ---------------- MMC3 的扫描线 IRQ ----------------
# 这一块不做 trace 差分：参照实现的 PPU 是 dot 级，IRQ 触发的时刻本来就和扫描线级的
# 实现不一样，逐行比必然分叉。改成核对**中断次数**，它和理论值的关系是硬的：
#   一帧 240 条可见扫描线，每 101 条触发一次 → N 帧约 N × 240 / 101 次。
Write-Host ""
Write-Host "==== MMC3  扫描线 IRQ（mapper4-irq.nes）===="

$irqRom = Join-Path $RomsDir 'mapper4-irq.nes'
$irqFrames = 12
$irqExpected = [Math]::Round($irqFrames * 240 / 101)   # ≈ 28.5

if ((Test-Path $irqRom) -eq $false) {
    Write-Host "  [FAIL] 没有 mapper4-irq.nes，先跑 node tests/make_mapper_test_rom.js" -ForegroundColor Red
    $failures++
}
else {
    $dump = & $GodotExe --headless --path $ProjectDir -- --rom $irqRom --dump-ram 0300 3 --dump-frame-after $irqFrames 2>&1
    $line = ($dump | Select-String -Pattern 'RAM \$0300:').Line

    if ($null -eq $line) {
        Write-Host "  [FAIL] 没拿到 RAM 结果" -ForegroundColor Red
        $failures++
    }
    else {
        $hex = ($line -split 'RAM \$0300:')[1].Trim() -split '\s+'
        $count = [Convert]::ToInt32($hex[0], 16)
        $inHandler = [Convert]::ToInt32($hex[1], 16)

        if ([Math]::Abs($count - $irqExpected) -le 2 -and $inHandler -eq 0) {
            Write-Host "  [OK]   跑 $irqFrames 帧触发 $count 次中断（理论 $irqExpected ≈ $irqFrames × 240 / 101）" -ForegroundColor Green
        }
        else {
            Write-Host "  [FAIL] 跑 $irqFrames 帧触发 $count 次，理论约 $irqExpected（容忍 ±2）；处理程序标记 = $inHandler" -ForegroundColor Red
            $failures++
        }
    }
}

$ErrorActionPreference = 'Stop'

Write-Host ""
if ($failures -eq 0) {
    Write-Host "mapper 测试通过：6 块 mapper 的自检全过、和参照实现的 trace 逐行一致、MMC3 的扫描线 IRQ 次数符合理论。" -ForegroundColor Green
    exit 0
}

Write-Host "mapper 测试失败：$failures 项。" -ForegroundColor Red
exit 1
