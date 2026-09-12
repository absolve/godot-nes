# PPU 差分测试：跑测试 ROM，把渲染出的画面和参照实现逐像素对比。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests/ppu_diff.ps1
#
# 做法：
#   1. 用 fogleman/nes（Go）跑 N 帧，把画面转成**调色板索引**输出（每字节 0-63）；
#   2. godot-nes 同样跑 N 帧，导出 PPU 的 IndexBuffer；
#   3. 逐字节比 —— 画面几何、调色板选择、图案取位、精灵叠加、翻转、优先级、滚动全在里面；
#   4. 再手算核对若干关键像素，防止"两边一起错"；
#   5. 核对 NMI：测试 ROM 的 NMI 处理程序会把 $0300 自增，跑 N 帧应该涨到 N-2 左右。
#
# 两个测试 ROM：
#   ppu-test.nes    滚动 (0,0)，水平镜像
#   ppu-scroll.nes  滚动 (3,5)，**垂直镜像** —— 这样才能同时验证滚动和名称表切换
#
# 参照实现的准备方法见 tests/README.md。

[CmdletBinding()]
param(
    [int]$Frames = 10,
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
$Oracle = Join-Path $WorkDir 'nesoracle\nesoracle.exe'
$RomDir = Join-Path $PSScriptRoot 'roms'

# 手算核对表：索引就是 PPU 输出的颜色号（从调色板 RAM 里读出来的那个字节）
$roms = @(
    @{
        Name = 'ppu-test.nes'
        Note = '滚动 (0,0)'
        Checks = @(
            @{ X = 0;  Y = 0;  Want = 0x01; Why = '左上角：颜色号1，属性左上→调色板0，$3F01' },
            @{ X = 4;  Y = 0;  Want = 0x02; Why = '同图块右半：颜色号2，$3F02' },
            @{ X = 16; Y = 0;  Want = 0x11; Why = '属性右上→调色板1，$3F05（列方向移位曾算错，专门盯这里）' },
            @{ X = 0;  Y = 16; Want = 0x21; Why = '属性左下→调色板2，$3F09' },
            @{ X = 16; Y = 16; Want = 0x31; Why = '属性右下→调色板3，$3F0D' },
            @{ X = 32; Y = 32; Want = 0x0A; Why = '精灵0 第0列，精灵调色板0，$3F11' },
            @{ X = 36; Y = 32; Want = 0x0B; Why = '精灵0 第4列，$3F12' },
            @{ X = 64; Y = 64; Want = 0x1B; Why = '精灵1 水平翻转后第0列=原第7列，$3F16' },
            @{ X = 68; Y = 64; Want = 0x1A; Why = '精灵1 翻转后第4列=原第3列，$3F15' }
        )
    },
    @{
        Name = 'ppu-scroll.nes'
        Note = '滚动 (3,5)，垂直镜像'
        Checks = @(
            @{ X = 0;   Y = 0; Want = 0x01; Why = '世界坐标 x=3 → 图块第 3 列，颜色号1，$3F01' },
            @{ X = 252; Y = 0; Want = 0x12; Why = '世界坐标 x=255 → 图块第 7 列，颜色号2，属性右半→调色板1，$3F06' },
            @{ X = 253; Y = 0; Want = 0x03; Why = '世界坐标 x=256 → **切到第 2 块名称表**（tile 2 纯色号3），$3F03' },
            @{ X = 255; Y = 0; Want = 0x03; Why = '同上，跨表区域最右一列' },
            @{ X = 32;  Y = 32; Want = 0x0A; Why = '精灵不受滚动影响，仍在 (32,32)，$3F11' },
            @{ X = 64;  Y = 64; Want = 0x1B; Why = '精灵1 翻转后第0列，$3F16' }
        )
    }
)

foreach ($rom in $roms) {
    if (-not (Test-Path (Join-Path $RomDir $rom.Name))) {
        Write-Host "测试 ROM 不存在，先生成…"
        & node (Join-Path $PSScriptRoot 'make_ppu_test_rom.js')
        break
    }
}

foreach ($path in @($Oracle, $GodotExe)) {
    if (-not (Test-Path $path)) {
        Write-Host "找不到：$path" -ForegroundColor Red
        Write-Host "参照实现和测试 ROM 的准备方法见 tests/README.md" -ForegroundColor Yellow
        exit 2
    }
}

if (-not (Test-Path $WorkDir)) {
    New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
}

$env:GOCACHE = Join-Path $WorkDir 'gocache'
$env:GOPATH = Join-Path $WorkDir 'gopath'
$env:GOPROXY = 'off'
$env:GOTOOLCHAIN = 'local'
$env:GOTELEMETRY = 'off'

$failures = 0

foreach ($rom in $roms) {
    $romPath = Join-Path $RomDir $rom.Name
    $refFrame = Join-Path $WorkDir 'ppu-ref.bin'
    $myFrame = Join-Path $WorkDir 'ppu-mine.bin'

    Write-Host ""
    Write-Host "==== $($rom.Name)  ($($rom.Note)) ===="

    # 原生程序会往 stderr 打印无害的警告（沙箱里写不了 user:// 之类），
    # 在 $ErrorActionPreference='Stop' 下会被当成终止错误，所以这两次调用期间放宽。
    $ErrorActionPreference = 'Continue'
    & $Oracle frame $romPath $Frames $refFrame 2>&1 | Out-Null
    $oracleExit = $LASTEXITCODE

    $mineOutput = & $GodotExe --headless --path $ProjectDir -- --rom $romPath `
        --dump-frame-indices $myFrame --dump-ram 0300 2 --dump-frame-after $Frames 2>&1
    $godotExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'

    if ($oracleExit -ne 0 -or $godotExit -ne 0) {
        Write-Host "执行失败（参照 $oracleExit / godot-nes $godotExit）" -ForegroundColor Red
        $failures++
        continue
    }

    $ref = [System.IO.File]::ReadAllBytes($refFrame)
    $mine = [System.IO.File]::ReadAllBytes($myFrame)

    if ($ref.Length -ne 61440 -or $mine.Length -ne 61440) {
        Write-Host "帧大小不对（参照 $($ref.Length) / godot-nes $($mine.Length)，应为 61440）" -ForegroundColor Red
        $failures++
        continue
    }

    # ---- 逐像素比 ----
    $diff = 0
    $firstDiff = -1
    for ($i = 0; $i -lt $ref.Length; $i++) {
        if ($ref[$i] -ne $mine[$i]) {
            $diff++
            if ($firstDiff -lt 0) { $firstDiff = $i }
        }
    }

    if ($diff -ne 0) {
        $x = $firstDiff % 256
        $y = [Math]::Floor($firstDiff / 256)
        Write-Host "  [FAIL] 画面有 $diff / $($ref.Length) 个像素不同；第一个 (x=$x, y=$y) 参照 $($ref[$firstDiff].ToString('X2')) / 这边 $($mine[$firstDiff].ToString('X2'))" -ForegroundColor Red
        $failures++
    } else {
        Write-Host "  [OK]   画面 $($ref.Length) 个像素逐字节一致" -ForegroundColor Green
    }

    # ---- 手算核对 ----
    foreach ($check in $rom.Checks) {
        $index = ($check.Y * 256) + $check.X
        $actual = $mine[$index]
        if ($actual -eq $check.Want) {
            Write-Host ("  [OK]   ({0,3},{1,3}) = {2:X2}   {3}" -f $check.X, $check.Y, $actual, $check.Why)
        } else {
            Write-Host ("  [FAIL] ({0,3},{1,3}) = {2:X2}，期望 {3:X2}   {4}" -f `
                $check.X, $check.Y, $actual, $check.Want, $check.Why) -ForegroundColor Red
            $failures++
        }
    }

    # ---- NMI ----
    $ramLine = ($mineOutput | Select-String -Pattern 'RAM \$0300').Line
    if ($ramLine -match 'RAM \$0300: ([0-9A-F]{2})') {
        $nmiCount = [Convert]::ToInt32($Matches[1], 16)
        # ROM 要先等两次 VBlank 才开始配置，所以 NMI 次数大约是 帧数-2
        if ($nmiCount -ge ($Frames - 4) -and $nmiCount -le $Frames) {
            Write-Host "  [OK]   NMI 计到 $nmiCount 次（跑 $Frames 帧）—— VBlank 中断正常" -ForegroundColor Green
        } else {
            Write-Host "  [FAIL] NMI 只计到 $nmiCount 次，跑 $Frames 帧时应接近 $Frames" -ForegroundColor Red
            $failures++
        }
    } else {
        Write-Host "  [FAIL] 没拿到 RAM 转储，无法核对 NMI" -ForegroundColor Red
        $failures++
    }
}

# ---- 精灵 0 命中（单独一块 ROM）----
# 这块 ROM 的精灵 0 带"在背景后面"的属性、又压在整屏不透明的背景上。
# 真机上"精灵 0 命中"和优先级无关，标志照样置位；先判优先级再判命中的话永远不置位，
# SMB 的标题画面就会死等这个标志（表现为精灵不出现、按开始没反应）。ROM 自己轮询 $2002 的
# bit6，命中写 $0300 = $A5，超时写 $00。
Write-Host ""
Write-Host "==== ppu-sprite0.nes（精灵 0 命中的优先级）===="

$sprite0Rom = Join-Path $PSScriptRoot 'roms\ppu-sprite0.nes'
if ((Test-Path $sprite0Rom) -eq $false) {
    Write-Host "  [FAIL] 没有 ppu-sprite0.nes，先跑 node tests/make_ppu_test_rom.js" -ForegroundColor Red
    $failures++
}
else {
    # 原生程序会往 stderr 打无害警告（沙箱里写不了 user://），调用期间放宽错误处理
    $ErrorActionPreference = 'Continue'
    $spriteOutput = & $GodotExe --headless --path $ProjectDir `
        -- --rom $sprite0Rom --dump-ram 0300 1 --dump-frame-after 3 2>&1
    $ErrorActionPreference = 'Stop'

    $spriteLine = ($spriteOutput | Select-String -Pattern 'RAM \$0300').Line

    if ($spriteLine -match 'RAM \$0300: ([0-9A-F]{2})' -and $Matches[1] -eq 'A5') {
        Write-Host "  [OK]   精灵 0 在背景后面时照样置命中标志（`$0300 = A5）" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] 精灵 0 命中没置位（$spriteLine）—— 检查 RenderSprites 里命中和优先级的先后顺序" -ForegroundColor Red
        $failures++
    }
}

Write-Host ""
if ($failures -gt 0) {
    Write-Host "PPU 差分测试失败：$failures 项" -ForegroundColor Red
    exit 1
}

Write-Host "PPU 差分测试通过：两个 ROM 的画面都逐像素一致，手算核对和 NMI 也都正常。" -ForegroundColor Green
exit 0
