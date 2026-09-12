# tests

放测试 ROM、冒烟测试和逐阶段验收用的对照数据。
只有 `roms/` 被 `.gdignore` 排除（ROM 体积大、也不需要变成 Godot 资源）；
`smoke_test.tscn` 是可被 Godot 加载的场景。

> **关于本文里的路径**：`<Godot 目录>`、`<项目目录>`、`<ROM 路径>` 都是占位符，
> 替换成你自己机器上的实际路径即可（例如 `<Godot 目录>` 换成你解压 Godot 的位置）。
> 为了可读性，示例里没有把每条命令都写成 PowerShell 变量，复制后改一下路径就能用。
## 目录

- `smoke_test.tscn` / `smoke_test.gd`：无头冒烟测试，144 项断言（含手柄 1 / 手柄 2 输入的端到端检查）。
- `cpu_diff.ps1`：**CPU 差分测试** —— 拿 fogleman/nes（Go）当参照，对同一个 ROM 逐行比对
  PC / 寄存器 / 标志位 / 周期数。
- `ppu_diff.ps1`：**PPU 差分测试** —— 比对渲染出的画面，逐像素；外加手算核对、NMI、精灵 0 命中。
- `apu_test.ps1`：**APU 测试** —— 奏已知频率的音，再数过零点量回来。
- `mapper_test.ps1`：**mapper 测试** —— 换 bank 自检（手算期望值）+ 和参照实现逐行 trace 差分
  + MMC3 扫描线 IRQ 次数核对。
- `../tools/probe_perf.gd`：**性能探针**（`godot --path godot-nes res://tools/probe_perf.tscn -- <rom>`）——
  带窗口量 NES 帧率 / 卡顿 / 音频丢样本数。速度只能在带窗口的模式下量，无头模式故意不节流。
- `make_torture_rom.js`：生成 `torture.nes`，把官方指令和寻址模式全跑一遍的"酷刑 ROM"。
- `make_ppu_test_rom.js`：生成 `ppu-test.nes` / `ppu-scroll.nes` —— 真正会配 PPU 并画图的 6502 程序。
- `make_apu_test_rom.js`：生成 4 个音频 ROM（静音 / 440 Hz 脉冲 / 220 Hz 三角波 / 噪声）。
- `make_mapper_test_rom.js`：生成 6 个 mapper ROM（mapper 1/2/3/4/7 + MMC3 扫描线 IRQ）。
- `make_test_rom.ps1`：生成 7 个合成 `.nes` 当解析器 / mapper 支持列表的测试样本（没有真实 ROM 时也能验证整条链路）。
  | 文件 | 内容 | 期望 |
  |:---|:---|:---|
  | `synthetic-nrom.nes` | iNES 1.0 / mapper 0 / 16KB PRG + 8KB CHR | 解析成功 |
  | `synthetic-nes20.nes` | NES 2.0 / mapper 0 / 32KB PRG + 8KB CHR RAM，submapper 1、8KB WRAM、4KB NVRAM、PAL、电池 | 解析成功 |
  | `synthetic-mapper2.nes` | iNES 1.0 / mapper 2 (UxROM) / 8×16KB PRG | 解析成功（UxROM 已实现） |
  | `synthetic-mapper4.nes` | iNES 1.0 / mapper 4 (MMC3) | 解析成功（MMC3 已实现） |
  | `synthetic-mapper5.nes` | iNES 1.0 / mapper 5 (MMC5) | 被拒绝：暂不支持 Mapper 5 |
  | `synthetic-truncated.nes` | 头里声明 32KB PRG，实际只给 16KB | 被拒绝：文件被截断 |
  | `synthetic-badmagic.nes` | 开头不是 `NES\x1A` | 被拒绝：找不到 iNES 魔数 |
- `roms/`：测试 ROM。**ROM 文件不入库**（版权原因，见仓库根 `.gitignore`），自己放进来即可。
- **差分排查工具**（见文末「差分排查工具」一节）：录制/重放按键、逐帧 RAM 对比、FCEUX 存档解析、
  CHR/名称表字节级核对 —— 查"画面/状态对不上"时照那节走。
- `logs/`：从模拟器导出的执行日志，用来跟官方日志对比。

## 冒烟测试

```powershell
# 先设一次 Godot 控制台程序的位置（脚本和下面所有命令都用它）
# 也可以做成系统环境变量，或给脚本传 -GodotExe
$env:GODOT = "<Godot 目录>\Godot_v4.7.2-stable_mono_win64_console.exe"
$godot = $env:GODOT

# 先准备一个 ROM（没有真实 ROM 就用合成的）
powershell -NoProfile -ExecutionPolicy Bypass -File tests\make_test_rom.ps1

& $godot `
  --headless --path "<项目目录>\godot-nes" "res://tests/smoke_test.tscn"
echo "退出码 $LASTEXITCODE"      # 0 = 全部通过
```

它验证的是「GDScript 主界面 → C# EmulatorCore 热路径 → 独立调试窗口」这条链：

1. autoload（`EmulatorService` / `DebugHub` / `UiSettings`）都在；
2. 主场景能加载，顶部有 6 项菜单（文件/模拟/视图/调试/语言/帮助）、有 Display 和状态栏；
3. 三个调试窗口存在且默认隐藏，且 `embed_subwindows` 已被关掉；
4. `LoadRom` 成功，主界面的 `_process` 真的在驱动 C# 跑帧；
5. **没有窗口打开时 `DebugHub` 零订阅**，打开后订阅数变成 1 / 2 / 3，关掉又回到 0；
6. CPU / PPU / ROM 窗口里的标签确实被快照填上了内容；
7. **ROM 解析**：iNES 1.0 与 NES 2.0 的各个字段（mapper、submapper、PRG/CHR bank、
   CHR RAM、WRAM/NVRAM、制式、电池、向量、CRC32、MD5）都读对了；
8. **错误路径**：不支持的 mapper、截断文件、魔数不对都要被拒绝，而且原因看得懂；
9. **关闭 ROM**：卸载后 `HasRom` 为假、信息被清空、而且不再推进帧；
10. **主题**：主界面和调试窗口都挂上了主题、菜单栏底色是 `#292a2d`（不是黑的）、
    字号 17 / 15、`MonoLabel` 真取到等宽字体、`DimLabel` 的文字更暗；
11. **国际化**：真刀真枪切一次语言（中 → 英 → 中），检查菜单标题、窗口标题、tscn 里的
    静态表头、带 `%s` 占位符的翻译、英文值结尾的空格、同一个 key 的两种用法，
    以及切回中文后是否都恢复。

> 国际化的断言要**真的切语言**才算数：Godot 的 `TranslationServer` 在当前 locale 查不到翻译时
> 会退到 fallback（默认 `en`），只测"翻译表能加载"是完全发现不了的（踩过一次，见实现文档 §12.6）。
>
> 主题同理，要断言**控件查出来的值**（`get_theme_stylebox` / `get_theme_font_size`），
> 而不是"我设了 root.theme" —— 根 Window 的 theme 根本传不下去（见实现文档 §12.5）。

单独看某个 ROM 的解析结果（不用跑整条链）：

```powershell
& $godot `
  --headless --path "<项目目录>\godot-nes" `
  -- --rom "<项目目录>\godot-nes\tests\roms\synthetic-nes20.nes" --rom-info
```

## CPU 差分测试

CPU 是模拟器里最容易写出"看起来对、其实错一个周期或错一个标志位"的地方，
所以这里不靠肉眼看日志，而是**和一个成熟的第三方实现逐行对**。

参照实现是 [fogleman/nes](https://github.com/fogleman/nes)（Go，MIT）：
它的 CPU 字段全部导出，可以精确对齐初始状态，而且只依赖标准库，离线就能编译。

```powershell
# 首次准备（参照实现不随仓库分发，见下面「准备参照实现」）
powershell -NoProfile -ExecutionPolicy Bypass -File tests\cpu_diff.ps1
echo "退出码 $LASTEXITCODE"      # 0 = 完全一致
```

脚本做三件事：

1. `make_torture_rom.js` 生成的 `torture.nes` 里，**把官方指令和寻址模式全跑一遍**：
   - 160 条官方非控制流指令各执行一次；
   - 索引寻址故意跨 256 字节页（读类该多 1 周期、写类不该多）；
   - 零页间接 `(zp,X)` / `(zp),Y`；
   - 分支的四种情况：不跳、跳、跨页跳；JSR/RTS、PHA/PLA、PHP/PLP、TSX/TXS；
   - BRK → IRQ 向量 → RTI 返回；最后是无条件跳转自循环。
2. 参照实现和 godot-nes 各跑一遍，按同一种格式打印**每条指令执行前**的状态：
   `PC OP B1 B2 A X Y P SP CYC`。
3. 逐行比字符串 —— 寄存器、标志位、周期数任意一处不一致都会报出来并指出行号。

> 指令清单和指令长度是**从参照实现的表里读出来的**，不是从 godot-nes 自己的表来的 ——
> 否则就成了拿自己印证自己。

### 准备参照实现

参照实现放在仓库根的 `.ref/`（已加入 `.gitignore`，不入库）：

```powershell
# 1) 复制 fogleman/nes 的 nes 包（只依赖标准库）
New-Item -ItemType Directory -Force <项目目录>\.ref\nesoracle\nes | Out-Null
Copy-Item <fogleman/nes 源码目录>\nes\*.go <项目目录>\.ref\nesoracle\nes\ -Force
# 2) 再放两个文件：go.mod 和 main.go（trace 脚手架），以及 nes\oracle_export.go
#    它们的内容见「实现文档」的 CPU 一节；go.mod 只需 module nesoracle / go 1.21
cd <项目目录>\.ref\nesoracle
$env:GOCACHE="<项目目录>\.ref\gocache"; $env:GOPROXY="off"; $env:GOTOOLCHAIN="local"; $env:GOTELEMETRY="off"
go build -o nesoracle.exe .
```

`main.go` 里除了 `<rom> <条数> <输出>`（CPU trace）和 `frame <rom> <帧数> <输出>`（索引帧），
还有两个为查问题加的模式，将来复现时一并保留：

| 模式 | 用途 |
|:---|:---|
| `ram <rom> <帧数> <地址hex> <长度> <输出>` | 跑 N 帧后 dump 一段 CPU RAM，两边逐字节比"游戏内部状态" |
| `traceframe <rom> <条数> <输出>` | 和 CPU trace 同格式，但**整机一起跑**（PPU/APU 推进、NMI/IRQ 照常递送） |

> 另外 `nes/ppu.go` 的 `Reset()` 被改成从扫描线 0 开始（原本是 `Cycle=340 / ScanLine=240`）。
> 不改的话，参照实现复位后几乎立刻就进 VBlank，而 godot-nes 要跑满一帧 ——
> "第 N 条指令时 PPU 走到哪儿"对不上，整机 trace 从第 8 行就分岔，没法查真正的分歧。
> 这只影响上电后第一帧的长度，游戏逻辑不受影响。

## PPU 差分测试

PPU 更容易"看起来画出来了、其实某个像素错了一色"。这里同样不靠肉眼，而是和参照实现
**逐像素比**：两边各跑 N 帧，把画面转成调色板索引（每字节一个颜色号），然后逐字节比。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests\ppu_diff.ps1
echo "退出码 $LASTEXITCODE"      # 0 = 完全一致
```

两个测试 ROM 都是**真正的 6502 程序**（不是空 NOP），会自己配好 PPU 并开渲染：

| ROM | 内容 | 覆盖到的东西 |
|:---|:---|:---|
| `ppu-test.nes` | 整屏 tile 0（左 4 列颜色号 1、右 4 列颜色号 2）、属性表全 `$E4`（四色调色板 16x16 棋盘格）、两个精灵（一个水平翻转）、OAM 走 `$4014` DMA、开 NMI | 名称表、图案取位、属性表四个字段、精灵叠加/翻转/调色板、DMA、VBlank/NMI |
| `ppu-scroll.nes` | 同上，但滚动 (3,5)，**垂直镜像**，第二块名称表填了不同的图块 | 精细/粗粒度滚动、跨名称表取图块（x≥253 时会切表）、非零滚动下的属性选择 |
| `ppu-sprite0.nes` | 整屏不透明背景 + 精灵 0 带"在背景后面"属性。ROM 自己轮询 `$2002` 的 bit6，命中写 `$0300 = $A5`、超时写 `$00` | **精灵 0 命中与优先级无关**（这块就是那个让 SMB 卡死的 bug 的回归测试） |

> 滚动版必须用**垂直镜像**：水平镜像下 `$2400` 和 `$2000` 会落到同一块物理名称表上，
> 那样"名称表切换"根本测不到（踩过一次）。

脚本除了逐像素比，还会：

- **手算核对** 15 个关键像素（每个都能从"图块图案 + 属性 + 调色板"推出来），
  防止"两边一起错"；
- 核对 **NMI**：测试 ROM 的 NMI 处理程序把 `$0300` 自增，跑 10 帧应该计到 7~8 次；
- 核对**精灵 0 命中**（`ppu-sprite0.nes`）。

> 比画面时要注意：参照实现的 `frame` 模式输出的是**调色板索引**，而它是把 RGBA 反查回索引的 ——
> NES 调色板里有重复颜色（`$20` 和 `$30` 都是纯白），直接比索引会看到一堆"假不同"。
> 要对比真实 ROM 的画面，用 `tools/idx2png.gd` 的 `diff` 模式**按 RGB 比**（它就是为查 SMB 写的）。

## APU 测试

音频没有"逐字节可比"的参照 —— 各家模拟器的混音单位和滤波都不一样。所以这里换个思路：
**让测试 ROM 奏一个频率已知的音，把输出导出来，再数过零点把频率量回来**。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests\apu_test.ps1
echo "退出码 $LASTEXITCODE"      # 0 = 全部符合
```

| ROM | 内容 | 判据 | 实测 |
|:---|:---|:---|:---|
| `apu-silent.nes` | 什么都不开 | 输出**精确为 0** | 0 ✓（证明混音+直流消除之后没有偏移） |
| `apu-pulse.nes` | 脉冲 1，理论 440.4 Hz | 频率 ±2% 内 | 440.6 Hz ✓ |
| `apu-triangle.nes` | 三角波，理论 220.2 Hz | 频率 ±2% 内 | 220.4 Hz ✓ |
| `apu-noise.nes` | 噪声 | 有声音且过零点够多 | 峰值 5711、过零 41814 ✓ |
| `apu-dmc-off.nes` | DMC 播一小段，然后 `$4015` 关掉 | 开头要有声音、**最后 0.5 秒峰值必须 ≈ 0** | 开头 14904、尾段 **0** ✓ |

理论频率来自 `timer = 253`：脉冲 `1789773/(16*254)`、三角波 `1789773/(32*254)`。

> 数过零点要用**中值**当阈值，不能拿 0 —— 混音输出是单极性的（0 到正的峰值），
> 拿 0 当阈值一个都数不到（踩过一次）。
>
> `apu-dmc-off.nes` 是"关掉之后必须安静"的回归测试：它抓的是 DMC 关掉后移位器
> 还在拿最后一个字节反复抖 DAC 的 bug（听感是持续背景嘶声）。
> **验证过它真能抓住**：把那段行为临时加回去，尾段峰值变成 120、用例报 FAIL，
> 而且其它四个用例（静音/脉冲/三角/噪声）也一起挂了 —— 正好复现"嘶声盖住整段音乐"。

## 真实 ROM 的按键 / 音频差分（拿 jsnes 当裁判）

跑真实 ROM（比如 SMB）时，"按键有没有生效""声音对不对"这种事没法用手算核对，办法是**和另一个
独立实现比**：jsnes 是纯 JS、能直接 headless 跑，正好当第三方裁判。

```powershell
# 同一条按键脚本下比 CPU RAM（$0000-$07FF）
& $godot --headless --path godot-nes -- --rom $rom --dump-ram 0 2048 --dump-frame-after 400 `
    --press down 150 220 --press start 250
node .ref\jsnes-ram.mjs "$rom" 400 .ref\ram-jsnes.bin "down:150-220,start:250"
node .ref\ram-diff.js .ref\ram-mine.bin .ref\ram-jsnes.bin

# 同一条按键脚本下比音频：先比响度包络，再比过零率（音高）
node .ref\pcm-corr.js .ref\play-mine.pcm .ref\play-jsnes.pcm
```

要点：

- **`--press <键> <按下帧> [松开帧]`** 是给无头测试用的钩子；没有它就没法在命令行里"开始游戏"。
- 比 RAM 之前先确认自己两次运行结果一致（确定性），再和自己比 —— 跨模拟器比时**只比"同一条脚本下
  状态变没变"**，不要指望两边逐字节相同（NMI 相位、输入采样点会差个一两帧）。
- 音频**不要比波形/响度**（各家混音曲线不同），比**过零率**：同一段旋律下相关系数应该接近 1
  （SMB 实测 0.975、最佳滞后 0 块；响度包络相关 0.785）。
- 查"背景嘶声"这类问题，用 `.ref/pcm-blocks.js` 逐块看 RMS/峰值/过零：
  音乐里的**休止段必须真的到 0**（SMB 实测和 jsnes 一致，两边都是 0.0000）。
  一直有个底噪下不去，就说明有通道关不干净（典型是 DMC 的移位器没停）。

## mapper 测试

mapper 的坑是"换 bank 换错了但画面要过一会儿才看得出来"，所以不靠肉眼，而是让 6502 程序自己去戳：
每块 PRG bank 开头放一个特征字节、每块 CHR 的 tile 1 里写"我是第几块"，然后**真的写换 bank 寄存器、
再从窗口读回来**和手算的期望值比（CHR 从 CPU 也能读：PPU 的 `$0000-$1FFF` 就是图案表，走 `$2006/$2007`）。

```powershell
# 先生成那 6 块 ROM
node tests/make_mapper_test_rom.js
powershell -NoProfile -ExecutionPolicy Bypass -File tests\mapper_test.ps1
echo "退出码 $LASTEXITCODE"      # 0 = 全过
```

| ROM | 覆盖的东西 | 自检条数 |
|:---|:---|:---|
| `mapper1.nes` | MMC1：PRG 模式 0/1/2/3、五种镜像、CHR 8KB/4KB 两种模式 | 20 |
| `mapper2.nes` | UxROM：16KB 换 bank、$C000 固定最后一块、超范围取模 | 7 |
| `mapper3.nes` | CNROM：写 `$8000` 只换 CHR（PRG 不动）+ CHR 8KB 换块 | 12 |
| `mapper4.nes` | MMC3：R6/R7、PRG 模式 1、`$A000` 镜像、CHR 两种模式（含两半对调） | 21 |
| `mapper7.nes` | AxROM：32KB 换 bank、单屏镜像高低切换、CHR RAM | 6 |
| `mapper4-irq.nes` | MMC3 扫描线 IRQ：跑 12 帧应该触发 28 次左右（理论 12 × 240 / 101） | — |

每个 ROM 都走两道关，**缺一不可**：

1. **自检**：期望值是照着 nesdev 手算的，失败条数留在 `$03F0`（必须 0），实际值留在 `$0310+X` 起；
2. **差分**：同样这几个 ROM 和 fogleman/nes 逐行比 CPU trace（每条不等就多执行一条 `INC`，
   所以换错 bank 会让两条 trace 立刻错开）。

> 只做差分，两边一起错就发现不了（比如都以为 MMC3 复位后 `$A000` 是第 1 块）；
> 只做自检，人算错了会以为自己错了。两道关一起过才算数。
>
> MMC3 的扫描线 IRQ 不做差分：参照实现的 PPU 是 dot 级，IRQ 触发时刻本来就不一样，逐行比必然分叉 ——
> 改成核对**中断次数**，它和理论值的关系是硬的。

## 各阶段要用的测试 ROM

| 阶段 | 测试 ROM | 验收标准 |
|:---|:---|:---|
| 1 CPU | `nestest.nes` | 与 `nestest.log` 逐行对比 PC/A/X/Y/P/SP/CYC，前 5000 行完全一致 |
| 3 PPU 基础 | `ppu_vbl_nmi.nes` | VBlank / NMI 时序测试全过 |
| 4 精灵 | `sprite_hit_tests`、`sprite_overflow_tests` | 精灵 0 命中、精灵溢出行为正确 |
| 4 PPU 综合 | `blargg_ppu_tests` | 背景、滚动、镜像正确 |
| 5 输入 | 《超级马里奥兄弟》 | 标题画面 → 1-1 关卡可操作 |
| 7 Mapper | 官方的 `mmc3_test`、`mapper1/2/3` 测试 ROM | 换 bank、IRQ 正确（自写的 `mapper_test.ps1` 已经覆盖了自检 + 差分，官方 ROM 是再加一道保险） |

## 快速拿到 nestest

`nestest.nes` 与配套日志在 <https://wiki.nesdev.org/w/index.php/Emulator_tests> 一节有链接，
把它放到 `tests/roms/nestest.nes`，再通过主界面「加载 ROM」或命令行参数装载：

```powershell
& $godot `
  --path "<项目目录>\godot-nes" -- --rom "<项目目录>\godot-nes\tests\roms\nestest.nes"
```


## 差分排查工具（查画面/状态问题的手册）

这一节是"某个 ROM 画面对不上、状态不对"时的操作流程。原理都一样：
**把可疑的那一段录下来 → 原样重放 → 和参照实现/FCEUX 做字节级对比**，不靠肉眼猜。

### 录制与重放（不用再手动玩一遍）

```powershell
# 1) 带窗口跑，操作会被录下来；界面上按 Q 会把当前帧的完整快照存下来
& $godot --path <项目目录>\godot-nes -- `
    --rom "<ROM 路径>" --log-input "<项目目录>\.ref\play-input.txt"

# 2) 之后任何时候都能用录制文件原样重放（无头、可重复）
& $godot --headless --path <项目目录>\godot-nes -- `
    --rom "<ROM 路径>" --press-file "<项目目录>\.ref\play-input.txt" `
    --dump-frame-after 4206 --dump-frame-indices "<项目目录>\.ref\f.idx"
```

按 `Q` 存下的快照共 6 个文件：`.idx`（画面）、`.nt`（4KB 名称表+属性表）、
`.chr`（当前映射出来的 8KB CHR）、`.oam`、`.pal`、`.ram` —— 可以离线把那一帧原样重画。

### 观测开关一览

| 开关 | 看到什么 |
|:---|:---|
| `--log-input` / `--press-file` | 录制 / 重放按键 |
| `--ram-trace` | 每帧 2KB 主 RAM（逐帧状态对比的基础） |
| `--log-mapper` | 映射器寄存器写，带**帧 + 扫描线**（MMC3 的 bank/IRQ 时机就看它） |
| `--log-ppu` / `--log-ppu-scroll` | PPU 寄存器写（带扫描线）+ 每条关键扫描线的卷轴状态 |
| `--log-apu` | `$4015` 读写 + 各通道长度计数器 |
| `--log-sprites` | 每行精灵评估数（eval/drawn/limit）+ 完整 OAM |
| `--trace-from-frame` + `--trace-console` | 从第 N 帧起打印指令 |
| `--dump-frame-indices` / `--dump-ram` / `--dump-audio` | 帧索引 / RAM / PCM 导出 |

按键位（与 fogleman、jsnes 一致）：bit0=A、1=B、2=Select、3=Start、4=Up、5=Down、6=Left、7=Right。

### 和 FCEUX 对比（它是最接近真机的参照）

1. FCEUX 里进到同一处，按 **`File → Save State`**（默认 `F5`），存档会写到它的 `fcs\` 目录；
2. `node .ref/fceux-state-parse.js <存档> <我的轨迹.bin>` —— 解压 `FCSX` 存档、取 2KB RAM，
   并自动在我的逐帧轨迹里搜最接近的一帧；
3. `node .ref/fceux-mmc3regs.js <存档>` —— 读 MMC3 的 `R0-R7`，核对 CHR/PRG bank；
4. `node .ref/find-nametable.js <存档> <我的.nt>` —— 按**段名**取出名称表逐行比对；
5. `node .ref/trace-vs-fceux.js <我的轨迹> <fceux轨迹>` —— 逐帧 RAM 对比，
   自动排除"两边初值不同"的未初始化字节。

### 画面类问题的常用工具

| 工具 | 用途 |
|:---|:---|
| `.ref/frame-shift.js` | 两帧上下平移，量化"整体上移/下移"（《Mighty Final Fight》就是靠它量出 34 行偏移） |
| `.ref/band-compare.js` | 把两帧的某条横带拼成一张图对比 |
| `.ref/render-bank.js` | 用指定 CHR bank 把名称表渲染成图（判断"该用哪个 bank"） |
| `.ref/chr-sheet.js` / `.ref/tile-by-bank.js` | 按 bank 看图块表 / 同一图块跨 bank 对比 |
| `.ref/chr-check.js` | 把映射出来的 8KB CHR 逐 1KB 对回 ROM 文件 |
| `.ref/ram-trace-diff.js` | 和 jsnes 的逐帧 RAM 对比（本项目不自动生成 jsnes 轨迹，需要时用 `jsnes-ramtrace.mjs`） |
| `tools/idx2png.tscn` | 帧索引 → PNG；`-- diff <a> <b> <out>` 出差异图并打印差异像素数 |

### 音频类问题

| 工具 | 用途 |
|:---|:---|
| `.ref/pcm-blocks.js` | 逐块 RMS / 峰值 / 过零（找"哪一段有噪声"） |
| `.ref/pcm-corr.js` | 和另一份 PCM 做包络/过零相关（判断音高与响度是否一致） |
| `.ref/jsnes-audio.mjs` | jsnes 侧音频导出（可带按键脚本），用来对照 |

### 两条方法论教训（都踩过，写在这里免得重犯）

1. **别把"1KB bank 视图"和"2KB bank 视图"混用。** MMC3 的 `R0` 覆盖 2KB（`$0000-$07FF`），
   含两个 1KB bank：图块 `$68` 的地址是 `$680`，落在**第二个** 1KB bank 里。
   拿"1KB bank `$79` 的图块 `$68`"去核对会读到空数据，容易误判成映射错误。
2. **不要用"在参照数据里搜索最像我的一段"证明两边一致** —— 循环论证。
   FCEUX 存档里的段有名字（`NTAR`/`REGS`/`RAM`…），按结构取出才是证据。

更完整的案例（底栏显示成顶部 HUD 的排查全过程）见实现文档 §16。
