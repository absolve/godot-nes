# 生成一组合成 .nes 文件，用来在没有真实 ROM 的情况下验证 ROM 解析器。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tests/make_test_rom.ps1
#
# 产物（都写到 tests/roms/，该目录不入库）：
#   synthetic-nrom.nes         iNES 1.0 / mapper 0 / 16KB PRG + 8KB CHR   → 应当解析成功
#   synthetic-nes20.nes        NES 2.0 / mapper 0 / 32KB PRG + 8KB CHR    → 应当解析成功
#                              （带 submapper、8KB WRAM、2KB 电池 NVRAM、PAL 制式、电池标志）
#   synthetic-mapper2.nes      iNES 1.0 / mapper 2 (UxROM) / 8×16KB PRG   → 应当解析成功
#   synthetic-mapper4.nes      iNES 1.0 / mapper 4 (MMC3)                 → 应当解析成功
#   synthetic-mapper5.nes      iNES 1.0 / mapper 5 (MMC5)                 → 应当被拒绝（没实现）
#   synthetic-truncated.nes    头里声明 32KB PRG，实际只给 16KB            → 应当被拒绝（截断）
#   synthetic-badmagic.nes     开头不是 "NES\x1A"                          → 应当被拒绝（格式不对）
#
# 这几个只验证「头解析 + mapper 认不认」，真正的换 bank 行为由 make_mapper_test_rom.js
# 生成的那几块 ROM 负责（见 tests/mapper_test.ps1）。
#
# 注意：本文件必须保存成"带 BOM 的 UTF-8"，否则 Windows PowerShell 5.1 会按 ANSI 读，中文注释会乱码。

[CmdletBinding()]
param(
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($OutDir)) {
    $directory = if ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { (Get-Location).Path }
    $OutDir = Join-Path $directory 'roms'
}

if (-not (Test-Path $OutDir)) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
}

$PRG_BANK = 0x4000   # 16 KB
$CHR_BANK = 0x2000   # 8 KB

# ---- 构造 PRG：填 NOP($EA)，末尾写向量表（$FFFA NMI / $FFFC RESET / $FFFE IRQ）----
function New-PrgRom {
    param([int]$Banks, [int]$ResetVector = 0x8000)

    $size = $Banks * $PRG_BANK
    $prg = [byte[]]::new($size)
    for ($i = 0; $i -lt $size; $i++) { $prg[$i] = 0xEA }

    $low = [byte]($ResetVector -band 0xFF)
    $high = [byte](($ResetVector -shr 8) -band 0xFF)
    $prg[$size - 6] = $low;  $prg[$size - 5] = $high    # NMI
    $prg[$size - 4] = $low;  $prg[$size - 3] = $high    # RESET
    $prg[$size - 2] = $low;  $prg[$size - 1] = $high    # IRQ
    return $prg
}

# ---- 构造 CHR：给每个图块一点规律，pattern table 预览里能看出图形 ----
function New-ChrRom {
    param([int]$Banks)

    $size = $Banks * $CHR_BANK
    $chr = [byte[]]::new($size)
    for ($tile = 0; $tile -lt ($size / 16); $tile++) {
        for ($row = 0; $row -lt 8; $row++) {
            $base = $tile * 16 + $row
            $chr[$base] = [byte](($tile * 8 + $row) -band 0xFF)
            $chr[$base + 8] = [byte]((($tile -shr 2) * 8 + $row) -band 0xFF)
        }
    }
    return $chr
}

function New-Header {
    param(
        [int]$PrgBanks, [int]$ChrBanks,
        [byte]$Flags6, [byte]$Flags7,
        [byte]$Byte8 = 0, [byte]$Byte9 = 0, [byte]$Byte10 = 0,
        [byte]$Byte11 = 0, [byte]$Byte12 = 0, [byte]$Byte13 = 0
    )

    $h = [byte[]]::new(16)
    $h[0] = 0x4E; $h[1] = 0x45; $h[2] = 0x53; $h[3] = 0x1A   # "NES\x1A"
    $h[4] = [byte]$PrgBanks
    $h[5] = [byte]$ChrBanks
    $h[6] = $Flags6
    $h[7] = $Flags7
    $h[8] = $Byte8
    $h[9] = $Byte9
    $h[10] = $Byte10
    $h[11] = $Byte11
    $h[12] = $Byte12
    $h[13] = $Byte13
    return $h
}

function Save-Rom {
    param([string]$Name, [byte[]]$Bytes)

    $path = Join-Path $OutDir $Name
    [System.IO.File]::WriteAllBytes($path, $Bytes)
    Write-Host ("  {0,-26} {1,9:N0} 字节" -f $Name, $Bytes.Length)
}

Write-Host "生成合成测试 ROM 到 $OutDir"

# --- 1. iNES 1.0 / mapper 0 / 16KB PRG + 8KB CHR -----------------------------
# Flags6: bit0=0 水平镜像, bit1=0 无电池, bit2=0 无 trainer, 高半字节=0 → mapper = 0
Save-Rom 'synthetic-nrom.nes' `
    ((New-Header -PrgBanks 1 -ChrBanks 1 -Flags6 0x00 -Flags7 0x00) + (New-PrgRom -Banks 1) + (New-ChrRom -Banks 1))

# --- 2. NES 2.0 / mapper 0 / 32KB PRG + 8KB CHR RAM（无 CHR ROM）------------
# Flags7 = 0x08    → bit3..2 = 0b10，识别为 NES 2.0；平台类型 = 0 (NES)
# Byte8  = 0x10    → 高半字节 submapper = 1，低半字节 mapper bit8-11 = 0
# Byte10 = 0x67    → 低半字节 PRG-RAM = 64<<7 = 8KB；高半字节 NVRAM = 64<<6 = 4KB
# Byte11 = 0x07    → 低半字节 CHR-RAM = 64<<7 = 8KB；高半字节 CHR-NVRAM = 0
# Byte12 = 0x01    → PAL 制式
# Flags6 = 0x02    → 电池标志（byte 6 的低半字节在两种格式里都是标志位）
# ChrBanks = 0     → 没有 CHR ROM，走 CHR RAM 那条路
Save-Rom 'synthetic-nes20.nes' `
    ((New-Header -PrgBanks 2 -ChrBanks 0 -Flags6 0x02 -Flags7 0x08 -Byte8 0x10 -Byte10 0x67 -Byte11 0x07 -Byte12 0x01) `
        + (New-PrgRom -Banks 2))

# --- 3. mapper 2 (UxROM) / mapper 4 (MMC3)：这两块已经实现了，应当解析成功 ----
# Flags6 高半字节 = mapper 号低 4 位
Save-Rom 'synthetic-mapper2.nes' `
    ((New-Header -PrgBanks 8 -ChrBanks 1 -Flags6 0x20 -Flags7 0x00) + (New-PrgRom -Banks 8) + (New-ChrRom -Banks 1))

Save-Rom 'synthetic-mapper4.nes' `
    ((New-Header -PrgBanks 2 -ChrBanks 1 -Flags6 0x40 -Flags7 0x00) + (New-PrgRom -Banks 2) + (New-ChrRom -Banks 1))

# --- 3b. mapper 5 (MMC5)：头能解析，但映射器还没实现，应当被明确拒绝 ----------
Save-Rom 'synthetic-mapper5.nes' `
    ((New-Header -PrgBanks 2 -ChrBanks 1 -Flags6 0x50 -Flags7 0x00) + (New-PrgRom -Banks 2) + (New-ChrRom -Banks 1))

# --- 4. 截断：头里声明 32KB PRG，实际只给 16KB -------------------------------
Save-Rom 'synthetic-truncated.nes' `
    ((New-Header -PrgBanks 2 -ChrBanks 1 -Flags6 0x00 -Flags7 0x00) + (New-PrgRom -Banks 1))

# --- 5. 魔数不对：开头是 "NOT" 而不是 "NES\x1A" -----------------------------
$bad = (New-Header -PrgBanks 1 -ChrBanks 1 -Flags6 0x00 -Flags7 0x00) + (New-PrgRom -Banks 1) + (New-ChrRom -Banks 1)
$bad[0] = 0x4E; $bad[1] = 0x4F; $bad[2] = 0x54
Save-Rom 'synthetic-badmagic.nes' $bad

Write-Host "完成。可以直接用 --rom-info 检查解析结果，例如："
Write-Host '  Godot_v4.7.2-stable_mono_win64_console.exe --headless --path <项目> -- --rom tests\roms\synthetic-nes20.nes --rom-info'
