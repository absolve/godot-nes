# godot-nes

用 **Godot 4（.NET / C# 版）** 从零写的 NES / Famicom 模拟器。

设计上有三条硬规矩：

1. **核心是纯 C#**（`godot-nes/core/`）—— 完全不认识 Godot，不引用任何 Godot 类型。好处是核心可以被无头测试直接驱动，也能整体搬到别的宿主里。
2. **每个子系统都拿成熟实现做差分验证**，不是"能跑就算"。CPU 对 fogleman/nes 逐行比 trace，PPU 逐像素比画面，APU 比音高与包络，mapper 比寄存器写日志，关键行为再对着 FCEUX 源码核对。
3. **性能有预算**：一帧模拟要明显低于 16.6 ms。现在实测 **7.05 ms/帧**（上传纹理 0.1 ms），无卡顿。

---

## 快速开始

### 环境要求

| 项 | 要求 | 说明 |
|:---|:---|:---|
| **Godot** | 4.7.x **.NET / Mono 版** | 普通版不含 C# 运行时，跑不了本项目 |
| **.NET SDK** | **10.0** | `godot-nes/godot-nes.csproj` 里是 `<TargetFramework>net10.0</TargetFramework>` |
| 系统 | Windows（推荐） | 测试脚本是 PowerShell；核心代码本身跨平台 |

### 跑起来

```powershell
# 用 Godot（.NET 版）打开工程：
#   <你的 Godot 目录>\Godot_v4.7.2-stable_mono_win64.exe
#   打开 godot-nes\project.godot ，按 F5 运行
# 然后在菜单「文件 → 打开 ROM…」里选一个 .nes（也可以直接把文件拖进窗口）
```

### 键位

| NES | 键盘 |
|:---|:---|
| 方向 | 方向键 或 **W / A / S / D** |
| A | **X** 或 **K** |
| B | **Z** 或 **J** |
| Start | **Enter** |
| Select | **Backspace** |
| 手柄 2 | 小键盘（`8/4/5/6` 方向、`1/2` = B/A、`3` = Start）—— 插上第二个手柄会自动分配 |

界面上还有：调试窗口（CPU / PPU / ROM 信息，菜单「调试」）、中英双语切换（菜单「语言」）、状态栏（可隐藏）。

### 命令行（无头模式）

```powershell
$godot = "<你的 Godot 目录>\Godot_v4.7.2-stable_mono_win64_console.exe"

# 看 ROM 信息（mapper / PRG / CHR / 镜像 / 制式）
& $godot --headless --path godot-nes -- --rom "某游戏.nes" --rom-info

# 跑 N 帧后把画面导出，再转 PNG（3 = 放大倍数）
& $godot --headless --path godot-nes -- --rom "某游戏.nes" --dump-frame-indices out.idx --dump-frame-after 600
& $godot --headless --path godot-nes "res://tools/idx2png.tscn" -- to-png out.idx out.png 3

# 比两帧，直接打印差异像素数（做差分测试用）
& $godot --headless --path godot-nes "res://tools/idx2png.tscn" -- diff a.idx b.idx diff.png
```

---

## 功能状态

| 模块 | 状态 | 说明 |
|:---|:---|:---|
| **CPU 6502** | ✅ | 官方 56 条指令全部实现，周期数按 nesdev 表；与 fogleman/nes 的 trace **800 行完全一致** |
| **PPU** | ✅ | 扫描线级渲染：背景 + 精灵（8×8 / 8×16、翻转、优先级、精灵 0 命中、溢出）、滚动、VBlank/NMI；测试 ROM 与参照实现**逐像素一致** |
| **APU** | ✅ | 5 个通道 + 帧计数器 + 非线性混音，接播放器出声；音高与理论值相符，静音段精确为 0 |
| **手柄** | ✅ | 两个手柄（键盘 + 手柄设备），读时序按真机（含读满 8 位之后的补 1 行为） |
| **mapper** | 7 块 | 见下表 |
| **界面** | ✅ | 深色主题（代码构建）、中英双语、状态栏、三个独立调试窗口 |
| **性能** | ✅ | 模拟 **7.05 ms/帧** + 上传 0.1 ms（预算 16.6 ms），0 次卡顿 |

### 支持的 mapper

| 编号 | 名称 | 备注 |
|:---|:---|:---|
| 0 | NROM | |
| 1 | MMC1 | PRG 四种模式 + 五种镜像 + CHR 8KB/4KB |
| 2 | UxROM | 16KB 换 bank + 最后一块固定 |
| 3 | CNROM | |
| 4 | MMC3 (TxROM) | 含扫描线 IRQ（状态栏分屏靠它）；CHR/PRG 映射与 FCEUX `FixMMC3CHR` 逐行对齐 |
| 7 | AxROM | |
| 23 | Konami VRC2b / VRC4e | 8KB PRG 换 bank + CHR 半字节高低位 + 镜像；实测《Contra (Japan) (Sample)》标题与片头正确 |

**已知简化（写在明处，别当 bug）**：

- PPU 是**扫描线级**而不是 dot 级 —— 同一扫描线行内改 `$2005/$2006/$2000` 只影响下一行；靠行内时序的 demo（彩虹效应、曼哈顿计划之类）会不对，普通游戏够用；
- 精灵 0 命中、精灵溢出都是行级精度；
- 没做 PPUMASK 的色彩强调位（只做了灰度位）；
- VRC4 的 `$F000` 扫描线中断没实现（Contra 是 VRC2 卡，用不到）；
- 未映射地址的读还没做开放总线，只做了 `$2002` 的低 5 位。

---

## 测试

全部测试都是**无头**的，不需要人看屏幕。测试 ROM 由脚本生成（**不含任何商业内容**），测试用的真实 ROM 自己放进 `tests/roms/` 即可（不入库）。

```powershell
# 先指定 Godot（.NET 版）控制台程序的位置；测试脚本会自动读这个环境变量
$env:GODOT = "<Godot 目录>\Godot_v4.7.2-stable_mono_win64_console.exe"

cd godot-nes

# 1) 生成测试 ROM（随时可重建）
powershell -NoProfile -ExecutionPolicy Bypass -File tests\make_test_rom.ps1
node tests\make_ppu_test_rom.js
node tests\make_apu_test_rom.js
node tests\make_mapper_test_rom.js
node tests\make_torture_rom.js

# 2) 冒烟测试（147 条断言）
& $env:GODOT --headless --path . "res://tests/smoke_test.tscn"

# 3) 四套差分测试
powershell -NoProfile -ExecutionPolicy Bypass -File tests\cpu_diff.ps1     # CPU 与 fogleman/nes 逐行比 trace
powershell -NoProfile -ExecutionPolicy Bypass -File tests\ppu_diff.ps1     # 画面逐像素比
powershell -NoProfile -ExecutionPolicy Bypass -File tests\apu_test.ps1     # 音高 / 过零 / 静音
powershell -NoProfile -ExecutionPolicy Bypass -File tests\mapper_test.ps1  # 6 块 mapper 自检 + trace 差分
```

| 套件 | 验证什么 | 当前结果 |
|:---|:---|:---|
| `smoke_test` | GDScript 主界面 → C# 热路径 → 调试窗口整条链路：菜单、主题、i18n、手柄 2、关闭 ROM 清屏、ROM 解析与错误路径 | **147/147** |
| `cpu_diff` | 和 fogleman/nes 逐行比 PC / 寄存器 / 标志 / 周期数 | 前 800 行完全一致 |
| `ppu_diff` | 渲染画面逐像素比 + 手算关键像素 + NMI 计数 | 两个 ROM 各 61440 字节全同 |
| `apu_test` | 静音为 0、脉冲 440 Hz、三角 220 Hz、噪声宽带、DMC 关掉后不残留抖动 | 5 个用例全过 |
| `mapper_test` | 6 块 mapper 的自检 ROM（换 bank / 镜像 / CHR 实际值）+ 与参照 trace 差分 | 全过 |

> **测试 ROM 的原理**：每一块都是**真正的 6502 程序**，运行时真的去写 mapper 寄存器，再把"换完之后从窗口读回来的字节"和手算期望值逐个比对，结果记在 `$03F0`（失败数）/ `$03F1`（条数）/ `$03F2`（完成标志 `$A5`）。这样既能抓住"和参照实现一起错"的情况，也能和参照 trace 差分。

---

## 排查工具

遇到"某个 ROM 画面对不上 / 声音不对 / 状态不对"时，这套流程能查到字节级 —— 原理是**把可疑的那一段录下来 → 原样重放 → 和参照实现做对比**，不靠肉眼猜。手册见 [`godot-nes/tests/README.md`](godot-nes/tests/README.md)，完整案例见 [`godot-nes-implementation.md`](godot-nes-implementation.md) §16。

### 录制与重放

```powershell
# 带窗口跑，操作会被录下来；界面上按 Q 会把当前帧的完整快照存下来
& $godot --path godot-nes -- --rom "某游戏.nes" --log-input .ref\play-input.txt

# 之后任何时候都能原样重放（无头、可重复，不用再手动玩一遍）
& $godot --headless --path godot-nes -- --rom "某游戏.nes" --press-file .ref\play-input.txt `
    --dump-frame-after 4206 --dump-frame-indices .ref\f.idx
```

按 `Q` 存下的是 **6 件套**：`.idx`（画面）、`.nt`（4KB 名称表 + 属性表）、`.chr`（当前映射出来的 8KB CHR）、`.oam`、`.pal`、`.ram` —— 可以离线把那一帧原样重画出来。

### 观测开关

| 开关 | 看到什么 |
|:---|:---|
| `--press <键> <按下帧> [松开帧]` | 脚本化按键（无头差分用） |
| `--press-file` / `--log-input` | 重放 / 录制按键 |
| `--ram-trace` | 每帧追加 2KB 主 RAM（逐帧状态对比的基础） |
| `--log-mapper` | 映射器寄存器写，带**帧 + 扫描线** |
| `--log-ppu` / `--log-ppu-scroll` | PPU 寄存器写（带扫描线）+ 每条关键扫描线的卷轴状态 |
| `--log-apu` | `$4015` 读写 + 各通道长度计数器 |
| `--log-sprites` | 每行精灵评估数 + 完整 OAM |
| `--trace-from-frame` + `--trace-console` | 从第 N 帧起打印指令 |
| `--dump-frame-indices` / `--dump-ram` / `--dump-audio` | 帧索引 / RAM / PCM 导出 |

按键位（与 fogleman/nes、jsnes 一致）：bit0=A、1=B、2=Select、3=Start、4=Up、5=Down、6=Left、7=Right。

### 对照工具（`.ref/` 下，随仓库分发）

| 工具 | 用途 |
|:---|:---|
| `fceux-state-parse.js` | 解析 FCEUX 即时存档（`FCSX` 头 + zlib 段），取出 2KB RAM |
| `fceux-mmc3regs.js` | 从存档读 MMC3 的 `R0-R7`，核对 CHR/PRG bank |
| `find-nametable.js` | 按**段名**从存档取名称表并逐行比对 |
| `chr-check.js` | 把"当前映射出来的 8KB CHR"逐 1KB 对回 ROM 文件 |
| `chr-sheet.js` / `tile-by-bank.js` | 按 bank 看图块表 / 同一图块跨 bank 对比（定位"字体在哪个 bank"） |
| `render-bank.js` | 用指定 CHR bank 把名称表渲染成图 |
| `frame-shift.js` | 两帧上下平移，量化"整体上移/下移" |
| `band-compare.js` | 把两帧的某条横带拼成一张图对比 |
| `ram-trace-diff.js` / `trace-vs-fceux.js` | 逐帧 RAM 对比，自动排除"两边初值不同"的未初始化字节 |
| `make-fceux-ramtrace.js` | 由录制的按键生成 FCEUX Lua 脚本（重放 + 逐帧写 RAM） |
| `pcm-blocks.js` / `pcm-corr.js` | 逐块 RMS / 峰值 / 过零、包络与过零相关（比音质） |
| `jsnes-*.mjs` | jsnes 侧的帧 / RAM / 音频 / 映射器日志导出 |

---

## 用到的第三方参照与工具

本项目**不包含**任何商业 ROM，也没有内嵌第三方模拟器源码。下面是开发时用来对照的参考资料（各自怎么准备见 `tests/README.md`）：

| 项目 | 语言 | 在本项目里的角色 |
|:---|:---|:---|
| [fogleman/nes](https://github.com/fogleman/nes) | Go | **主要差分参照**：CPU trace 逐行比、PPU 画面逐像素比、mapper 自检对照。仓库外准备一份打过补丁的副本（`.ref/nesoracle`），用于导出 trace / 帧 / RAM |
| [jsnes](https://github.com/bfirsh/jsnes) | JavaScript | **第二参照**：音频包络与过零相关、RAM 逐帧对比、mapper 写日志对比 |
| [FCEUX](https://github.com/TASEmulators/fceux) | C++ | **权威参照**：关键行为以它源码为准（MMC3 的 `FixMMC3CHR`、VRC2/VRC4 的 `VRC24Write`、手柄 `ReadGP`、MMC3 镜像的 `setmirror((V&1)^1)`）。它的即时存档能直接被上面那些脚本解析 |
| [VirtuaNES](https://github.com/emu-russia/VirtuaNES) | C++ | MMC3 中断计数器语义的第二意见 |
| olcNES | C++ | PPU / mapper 实现的补充参考 |

工具链本身：**Godot 4.7.x (.NET)** + **.NET SDK 10**（C#）、**Node.js**（测试 ROM 生成器与对照脚本）、**PowerShell**（测试驱动）、**Go**（编译参照实现）。

> 离线构建：`godot-nes/NuGet.config` 把包源指向本地目录，配合环境变量 `NUGET_PACKAGES` 可以完全不联网编译。首次构建如果没有本地包缓存，把 `NuGet.config` 里的本地源去掉即可走官方源。

---

## 目录结构

```
godot-nes/
├── core/              # 纯 C# 核心，不认识 Godot
│   ├── CPU/           # 6502
│   ├── PPU/           # 2C02（扫描线级）
│   ├── APU/           # 2A03 音频
│   ├── Bus/           # CPU 总线
│   ├── Cartridge/     # iNES / NES 2.0 解析 + mapper（Mappers/）
│   ├── Input/         # 手柄
│   └── NesConsole.cs  # 把上面几块拼成一台机器
├── script/            # Godot 层（C#）：EmulatorCore / Display / InputAdapter / NesAudioPlayer …
├── autoload/          # 单例：EmulatorService / DebugHub / UiSettings
├── ui/                # 界面层（GDScript）
├── scene/ theme/ lang/ sprite/ sound/ shader/   # 场景、主题、翻译、素材
├── tools/             # 调试工具场景（帧索引 → PNG、性能探针、图案表预览）
└── tests/             # 回归测试 + 测试 ROM 生成器 + 排查手册（ROM 本身不入库）
.ref/                  # 参照实现与排查脚本（大文件与缓存不入库，脚本入库）
```

依赖方向是单向的：`ui → autoload → script → core`，核心永不反向依赖。

---

## 文档

| 文档 | 内容 |
|:---|:---|
| [`godot-nes-implementation.md`](godot-nes-implementation.md) | **实现文档**：每个模块的状态、接口、关键决策、验证方式、踩过的坑（§16 是差分排查方法论） |
| [`godot-nes-ui-design.md`](godot-nes-ui-design.md) | 界面与独立调试窗口的设计、GDScript / C# 分工 |
| [`godot-nes-emulator-plan.md`](godot-nes-emulator-plan.md) | 分阶段开发计划与验收标准 |
| [`godot-nes/README.md`](godot-nes/README.md) | 工程结构、构建与运行细节 |
| [`godot-nes/tests/README.md`](godot-nes/tests/README.md) | 各测试套件怎么跑、测试 ROM 怎么写、排查工具手册 |
| [`NES Instructions.md`](NES%20Instructions.md) | 6502 指令速查 |

---

## 路线图

- [ ] 更多 mapper（MMC2 / MMC4 / VRC 系列其他变体 / MMC5 …）
- [ ] dot 级 PPU（按需 —— 现在扫描线级对绝大多数游戏够用）
- [ ] VRC4 的 `$F000` 扫描线中断
- [ ] 开放总线、PPUMASK 色彩强调
- [ ] 电池备份存档落盘

## 许可

见 [`LICENSE`](LICENSE)。
