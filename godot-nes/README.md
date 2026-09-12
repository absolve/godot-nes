# godot-nes

用 Godot 4.7（.NET / C# 版）实现的 NES 模拟器。

当前进度：**阶段 0 —— 项目骨架 + ROM 解析**。核心的 CPU / PPU / APU 还是桩，
画面显示的是诊断画面（见下面「阶段 0 验收」）。开发计划见仓库根的
[`godot-nes-emulator-plan.md`](../godot-nes-emulator-plan.md)。

## 环境

| 项目 | 版本 | 说明 |
|:---|:---|:---|
| Godot | 4.7.2 stable **mono** | 必须用带 .NET 的版本，`E:\Godot_v4.7.2-stable_mono_win64` |
| .NET SDK | 10.0.301 | 见下面「为什么目标框架是 net10.0」 |
| .NET 运行时 | 10.x（或 8.x / 9.x） | Godot 自己的 `GodotPlugins.runtimeconfig.json` 是 `net8.0` + `rollForward=LatestMajor` |

### 为什么目标框架是 net10.0

`GodotSharp.dll` 面向 `net8.0`，但本机只装了 .NET 7 / 9 / 10 运行时，没有 8.0，
也没有 net8.0 的 targeting pack（离线状态下拿不到）。既然 Godot 启动 CoreCLR 时用的是
`rollForward=LatestMajor`，直接编译到 `net10.0` 就能跑在已安装的 10.x 运行时上。
换一台装了 .NET 8 SDK 的机器时，把 `godot-nes.csproj` 里的 `<TargetFramework>` 改回
`net8.0` 即可，其余代码不用动。

### 离线 NuGet

`NuGet.config` 把 Godot 自带的包目录（`<Godot 安装目录>\GodotSharp\Tools\nupkgs`）
注册成了唯一的 NuGet 源，所以**断网也能 restore / build**。
如果以后要引用 nuget.org 上的第三方包，把该文件里注释掉的 `nuget.org` 源打开即可。

正常情况下不需要额外设置。只有在本机写不了全局包缓存（比如在受限沙箱里跑）时，才需要把
包缓存改到别处：

```powershell
$env:NUGET_PACKAGES = "E:\godot-nes\.nuget-packages"   # 该目录已加入 .gitignore
```

> 解决方案文件（`.sln`）不是必需的：Godot 构建的是 `.csproj`，命令行也是。
> 首次用编辑器打开项目时，如果它提示创建解决方案，接受即可（只是给 IDE 用的）。

## 目录结构

沿用 `machine-TD` 的组织方式（小写目录名，`scene/` + `script/` + `autoload/` 分离），
模拟器核心额外放在 `core/` 里：

```text
godot-nes/
├── project.godot          引擎配置：主场景、输入映射、程序集名
├── godot-nes.csproj       C# 工程（Godot.NET.Sdk 4.7.2）
├── NuGet.config           指向 Godot 自带 nupkg 的离线源
├── icon.svg               项目图标
├── autoload/
│   ├── EmulatorService.cs C# 单例：持有唯一的 NesConsole，负责装载/复位/暂停，并对外提供调试快照
│   ├── debug_hub.gd       GDScript 单例：以 12 Hz 拉快照并广播给可见的调试窗口（没窗口开就零开销）
│   └── ui_settings.gd     GDScript 单例：语言切换（样式在 theme/dark_theme.tres 里，不走代码）
├── theme/
│   └── dark_theme.tres    深色主题（Theme 资源）：配色、字号、样式盒、DimLabel/MonoLabel 变体
├── lang/
│   └── language.csv       翻译表：第一列是 key（= 中文原文），`en` 一列
├── scene/
│   └── main.tscn          主场景（Main → Ui[MenuBar/Display/StatusBar] + DebugWindows + EmulatorCore）
├── ui/                    GDScript 界面层
│   ├── main.gd            主界面：菜单、状态栏、快捷键、ROM 对话框、语言切换
│   ├── debug_window.gd    调试窗口基类（关闭即隐藏、按需订阅快照、切语言时重译）
│   └── windows/
│       ├── cpu_window.tscn/.gd   独立 CPU 窗口
│       ├── ppu_window.tscn/.gd   独立 PPU 窗口
│       └── rom_window.tscn/.gd   独立 ROM 信息窗口
├── script/                Godot 节点层里的 C# 部分（文件名必须和类名一致）
│   ├── EmulatorCore.cs    热路径：StepAndPresent() 一次调用跑完 采样输入+跑帧+上传纹理
│   ├── Display.cs         256x240 帧缓冲 → Image/ImageTexture → 屏幕（最近邻、等比居中）
│   ├── InputAdapter.cs    Godot 输入 → NES 手柄位
│   ├── NesAudioPlayer.cs  APU → AudioStreamGenerator（IAudioSink 实现）
│   └── PatternTablePreview.cs  PPU 窗口里的 pattern table 预览（CHR 解码留在 C# 侧）
├── core/                  **纯 C#，不引用任何 Godot 类型**，可以单独测试
│   ├── NesConsole.cs      CPU + PPU + APU + 总线 + 卡带的组合体
│   ├── Interfaces/        IBus / IRenderer / IAudioSink / IInputSource
│   ├── Bus/Bus.cs         CPU 内存映射（RAM 镜像、PPU/APU 寄存器、手柄、$4014 OAM DMA）
│   ├── CPU/               6502：寄存器、状态标志、寻址模式、官方 56 条指令、中断
│   ├── PPU/               NesPalette(64 色) + 寄存器、VRAM/调色板寻址、名称表镜像、扫描线渲染、精灵
│   ├── APU/Apu.cs         5 通道 + 帧计数器 + 非线性混音 + 采样
│   ├── Cartridge/         .nes 读取（iNES 1.0 + NES 2.0）、Mapper 基类/工厂 + 六块映射器（NROM/MMC1/UxROM/CNROM/MMC3/AxROM）
│   └── Input/Controller.cs 手柄移位寄存器（$4016 选通/移位）
├── theme/                 主题资源（dark_theme.tres）
├── tools/                 调试小工具（idx2png：索引帧 → PNG / 两张按 RGB 比差异）
├── shader/ sprite/ sound/   预留资源目录（目前为空）
└── tests/
    ├── smoke_test.tscn/.gd   无头冒烟测试（144 项断言，退出码 0 = 全过）
    ├── cpu_diff.ps1 / ppu_diff.ps1 / apu_test.ps1 / mapper_test.ps1   四个差分/物理量/自检测试
    ├── make_test_rom.ps1     生成 7 个合成 .nes（含 NES 2.0 和 3 个坏文件）当测试样本
    └── roms/                 测试 ROM（不入库；该目录有 .gdignore，Godot 不导入）
```

> 计划文档第 5 节里写的是 `scenes/` + `scripts/`，这里按 `machine-TD` 的习惯改成了
> `scene/` + `script/`，核心目录名 `core/` 保持一致。
> 界面相关的设计与取舍见仓库根的 `godot-nes-ui-design.md`，
> **每个模块的实现状态/接口/决策见 `godot-nes-implementation.md`**。

### 设计约定

1. **核心与引擎解耦**：`core/` 里连一个 `using Godot;` 都没有。Godot 只负责驱动
   （`StepFrame`）、显示（`FrameBuffer`）、音频（`IAudioSink`）和输入（`IInputSource`）。
2. **帧缓冲格式固定**：RGBA8、行优先、`256 * 240 * 4` 字节，正好能直接喂给
   `Image.SetData`，中间不做任何转换。
3. **热路径不分配**：`Display` 复用同一个 `Image`/`ImageTexture`，输入映射用静态数组，
   音频用预分配缓冲区。GC 抖动是模拟器的头号敌人。
4. **桩代码要能被发现**：没实现的地方都留了 `TODO(阶段 N)` 注释，并且会在 UI 上显示出来
   （例如 CPU 窗口里的「卡在未实现指令 $EA」）。
5. **语言分工：控制流和 UI 归 GDScript，热路径和数据归 C#**，每帧只跨一次语言。
   GDScript 只能看到 C# 里「public 且参数/返回值是 Variant 兼容类型」的方法与属性，
   纯 C# 类型（`NesConsole` / `Cpu` / `Ppu`）对它完全不可见 —— 所以调试数据统一走
   `EmulatorService` 的 `GetXxxSnapshot()` 摊平成 `Dictionary`。详见设计文档 §1。

## 构建与运行

### 在 Godot 编辑器里

用 `E:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe` 打开
`E:\godot-nes\godot-nes\project.godot`，按 `F5` 运行。编辑器里的「Build」按钮
就是调 `dotnet build`。

### 只用命令行

```powershell
# 编译
dotnet build E:\godot-nes\godot-nes\godot-nes.csproj

# 运行（带控制台输出，方便看日志）
& "E:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe" `
  --path "E:\godot-nes\godot-nes"

# 直接启动并装载 ROM
& "E:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe" `
  --path "E:\godot-nes\godot-nes" -- --rom "D:\roms\Super Mario Bros.nes"
```

### 命令行参数（写在 `--` 之后）

| 参数 | 说明 |
|:---|:---|
| `--rom <路径>` / `-r <路径>` | 启动时装载 ROM，也可以直接给一个 `.nes` 路径 |
| `--rom-info` | **只解析并打印 ROM 头信息然后退出**（无头验证用，解析失败退出码 1） |
| `--trace-cpu <条数>` | 不进帧循环，单步执行 N 条指令并写 trace 后退出（CPU 差分测试用） |
| `--trace-out <路径>` | 配合 `--trace-cpu` 指定输出文件 |
| `--dump-frame <png 路径>` | 跑若干帧后把帧缓冲写成 PNG 然后退出，用来在没有窗口的环境里看画面 |
| `--dump-frame-indices <路径>` | 同上，但写的是 PPU 的调色板索引帧（256×240 字节），适合和参照实现逐字节比 |
| `--dump-ram <地址> <长度>` | 打印一段 CPU RAM 的十六进制（如 `--dump-ram 0300 16`），核对副作用用 |
| `--dump-audio <路径>` | 导出 16 位单声道 PCM（44100 Hz），核对音高之类用 |
| `--dump-frame-after <帧数>` | 配合上面几个 dump 用，默认第 1 帧 |
| `--press <键> <按下帧> [松开帧]` | 无头测试钩子：从第 N 帧起按住手柄 1 的某个键（键名 `a`/`b`/`select`/`start`/`up`/`down`/`left`/`right`），可重复 |
| `--log-input <路径>` | **录制**每个按键变化（每行 `帧 按键位(hex)`）；界面按 `Q` 会把当前帧的 6 件套快照存到该文件所在目录 |
| `--press-file <路径>` | **重放** `--log-input` 录下来的操作（无头、可重复，排查不用再手动玩一遍） |
| `--ram-trace <路径>` | 每帧追加 2KB 主 RAM，用于和参照实现 / FCEUX 逐帧对比状态 |
| `--log-mapper <路径>` | 映射器寄存器写日志：`帧 扫描线 寄存器 值` |
| `--log-ppu <路径>` | PPU 寄存器写日志（同上格式） |
| `--log-ppu-scroll` | 在 `--log-ppu` 基础上再加每条关键扫描线的卷轴状态（`t` / fineX / 基准行） |
| `--log-apu <路径>` | `$4015` 读写 + 各通道长度计数器 |
| `--log-sprites <路径>` | 每行精灵评估数（eval/drawn/limit）+ 完整 OAM 表 |
| `--trace-from-frame <N>` | 配合 `--trace-console <条数>`：从第 N 帧起打印指令（不用从头 trace） |

### 键位

**手柄 1**

| NES | 键盘 | 手柄 |
|:---|:---|:---|
| 方向 | 方向键 / W A S D | 十字键 |
| A | X / K | A |
| B | Z / J | B |
| Start | Enter | Start |
| Select | Backspace | Back |

**手柄 2**（第二个手柄设备直接用就行）

| NES | 键盘 |
|:---|:---|
| 方向 | 小键盘 8 / 2 / 4 / 6 |
| A | 小键盘 1 |
| B | 小键盘 3 |
| Start | 小键盘 + |
| Select | 小键盘 0 |

> 想玩 2 人游戏（比如 SMB）：标题画面用方向键上 / 下选到 `2 PLAYER GAME` 后**松开方向键**再按 Start。
> 按住方向键的时候 Start 是不生效的，这是游戏本身的行为。

| 功能 | 键 |
|:---|:---|
| 加载 ROM | O |
| 复位 | R |
| 暂停 / 继续 | P |
| 单步一条指令 | F10 |
| 显示 / 隐藏状态栏 | F1 |
| CPU 窗口（独立窗口） | F2 |
| PPU 窗口（独立窗口） | F3 |
| ROM 信息窗口（独立窗口） | F4 |
| 全屏 | F11 |
| 退出 | Esc |

（还可以把 `.nes` 文件直接拖进窗口。）

## 界面结构

主窗口只有三样东西：**顶部菜单 + 游戏画面 + 底部一条细状态栏**。
CPU / PPU 的详细状态放在 **独立的 OS 窗口**里（`Window` 节点），用「视图」菜单或 F2 / F3 开关，
所以主界面永远不会被调试信息挤占。窗口关闭只是隐藏（不销毁），位置和状态都保留。

```text
文件    打开 ROM… / 关闭 ROM（没装 ROM 时置灰） / 退出
模拟    复位 / 暂停继续 / 单步一条指令
视图    CPU 窗口(F2) / PPU 窗口(F3) / ROM 信息窗口(F4) / 全屏(F11)   ← 带勾选状态
调试    显示状态栏(F1) / 当前帧存成 PNG
语言    中文 / English   ← 单选项，勾当前语言
帮助    快捷键 / 关于
```

独立窗口的前提是项目设置 `display/window/subwindows/embed_subwindows = false`
（它默认是 `true`，也就是子窗口嵌在主窗口里画）。这一条已经在 `project.godot` 里打开了，
冒烟测试里也有对应断言防止误改。

### 外观与语言

- **样式全在 `theme/dark_theme.tres` 里**（一份普通的 Godot `Theme` 资源），通过
  `project.godot` 的 `gui/theme/custom` 挂在项目主题上 —— 主窗口、独立调试窗口、对话框都自动生效，
  代码里没有任何设置样式的逻辑。改配色/字号直接开 Godot 的主题编辑器改那个文件。
  配色取自浏览器深色模式（**不是纯黑**）：窗口底 `#202124`、面板与菜单栏 `#292a2d`、
  文字 `#e8eaed`、强调色 `#8ab4f8`。**画面四周的留白也跟着走主题底色**（视口清屏色）。
- 字号比 Godot 默认（16）大一档半：正文 **20**，表头/说明 **18**（类型变体 `DimLabel`）；
  需要对齐的值用 `MonoLabel`（等宽字体），场景文件里不再写死字号和颜色。
- **独立调试窗口会跟随主窗口的缩放**：主窗口用 `canvas_items` 拉伸，而独立 `Window` 不跟着拉伸，
  所以主窗口放大之后调试窗口里的字会显小。`DebugWindow` 会把同一个缩放比抄过来，
  窗口尺寸按比例放大（不然内容会被裁掉）。冒烟测试带窗口那一遍会真的把主窗口放大到 2 倍来验证。
- **中英双语**：`lang/language.csv` 第一列是 key（也是中文原文），后面是各语言的译文；
  菜单「语言」里切换，启动时跟随系统语言。
  ⚠️ `project.godot` 里 `internationalization/locale/fallback` 必须保持为空字符串：
  Godot 查不到翻译时会退到 fallback（默认 `en`），那样中文界面会整片变成英文。

## ROM 读取（.nes 解析）

`core/Cartridge/Cartridge.cs` 负责把 `.nes` 文件拆成四块，并把"游戏相关信息"全部解析出来：

| 块 | 说明 |
|:---|:---|
| 16 字节头 | iNES 1.0 / NES 2.0 两种格式 |
| trainer | 可选 512 字节 |
| **PRG ROM（程序块）** | 映射到 CPU $8000-$FFFF，并从中读出 NMI / RESET / IRQ 向量 |
| **CHR ROM（图案块）** | 映射到 PPU $0000-$1FFF；大小为 0 时按 CHR RAM 处理 |

解析出来的信息：

- **卡带**：mapper 号 + 家族名（NROM / MMC1 / UxROM / MMC3 …）、子 mapper、名称表镜像、电池存档、trainer
- **程序块**：大小 / bank 数、复位向量与中断向量
- **图案块**：大小 / bank 数，或者"无 CHR ROM，用 N KB CHR RAM"
- **RAM**：PRG RAM / PRG NVRAM / CHR RAM / CHR NVRAM 大小
- **机器**：制式（NTSC / PAL / 多制式 / Dendy）、平台（NES / Vs. System / PlayChoice-10）
- **校验**：PRG+CHR 的 CRC32 和 MD5（和 FCEUX 报的是同一种，可以用来对照 ROM 数据库）

解析规则跟工作目录里的三个开源实现对过（FCEUX `src/ines.cpp`、fogleman/nes `nes/ines.go`、jsnes `src/rom.js`）。
其中最容易搞错的一点 —— **NES 2.0 的 mapper 高 4 位在 byte 8 的低半字节**（byte 6 的低半字节仍然是镜像/电池标志）
—— 三份实现互相印证。老写头工具留下的垃圾（`DiskDude!` / `demiforce` / `Ni03`）清理规则照抄 FCEUX。

结果有两种看法：

- 界面上：**视图 → ROM 信息窗口（F4）**，或者底部状态栏那一行概要；
- 命令行：`--rom-info`，无头也能看。

坏文件不会被静默吞掉，都会给中文原因，例如
`文件被截断：头里声明 PRG 32768 + CHR 8192 字节，实际只有 16384 字节（缺 24576 字节）。`

## 映射器（换 bank）

卡带里的 mapper 芯片决定"游戏怎么在有限的总线窗口里看到超出窗口的 ROM"。已实现的六块：

| mapper | 家族 | PRG | CHR | 镜像 | 中断 | 代表作 |
|:---|:---|:---|:---|:---|:---|:---|
| 0 | NROM | 固定 16/32KB | 固定 8KB | 固定 | — | 《超级马里奥兄弟》 |
| 1 | MMC1 (SxROM) | 16KB ×2，四种模式 | 4KB 或 8KB | ✅ 可改 | — | 《塞尔达传说》《银河战士》 |
| 2 | UxROM | $8000 可换 16KB | 固定 | — | — | 《魂斗罗》《洛克人》 |
| 3 | CNROM | 固定 | 8KB ×4 | — | — | 《冒险岛》 |
| 4 | MMC3 (TxROM) | 8KB ×4，两种模式 | 1KB ×8，两半可对调 | ✅ 可改 | **扫描线 IRQ** | 《马里奥 3》《星之卡比》 |
| 7 | AxROM (AOROM) | 32KB ×16 | 板载 CHR RAM | ✅ 单屏 | — | 《忍者蛙》 |

还没实现的 mapper 会在装载时给出明确提示，例如
`暂不支持 Mapper 5（已实现：0, 1, 2, 3, 4, 7, 23）。`

**MMC3 的扫描线 IRQ** 是分屏（状态栏固定、画面下半部分滚动）的唯一手段，本项目的 PPU 是扫描线级的，
所以中断按"每条可见扫描线一次"推进 —— 这是个**写在明处的近似**（真机数的是 A12 上升沿），做分屏够用。
已知的其他简化（CNROM 的总线冲突、MMC1 的 SXROM 大卡带扩展、MMC3 的 rev A/B 差异）见实现文档 §4.5。

## 验收

```powershell
# 1) 编译：应该 0 警告 0 错误
dotnet build E:\godot-nes\godot-nes\godot-nes.csproj

# 2) 生成合成测试 ROM（没有真实 ROM 时用）
powershell -NoProfile -ExecutionPolicy Bypass -File E:\godot-nes\godot-nes\tests\make_test_rom.ps1

# 3) 无头冒烟测试：144 项断言，退出码 0 = 全过
& "E:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe" `
  --headless --path "E:\godot-nes\godot-nes" "res://tests/smoke_test.tscn"
echo "退出码 $LASTEXITCODE"

# 4) CPU 差分测试：和 fogleman/nes 逐行比对 CPU 状态，退出码 0 = 完全一致
powershell -NoProfile -ExecutionPolicy Bypass -File E:\godot-nes\godot-nes\tests\cpu_diff.ps1
echo "退出码 $LASTEXITCODE"

# 5) PPU 差分测试：和 fogleman/nes 逐像素比对画面（两个 ROM，各 61440 像素），
#    外加手算核对关键像素和 NMI 计数
powershell -NoProfile -ExecutionPolicy Bypass -File E:\godot-nes\godot-nes\tests\ppu_diff.ps1
echo "退出码 $LASTEXITCODE"

# 6) APU 测试：奏已知频率的音再量回来（静音必须是 0，脉冲 440 Hz、三角波 220 Hz）
powershell -NoProfile -ExecutionPolicy Bypass -File E:\godot-nes\godot-nes\tests\apu_test.ps1
echo "退出码 $LASTEXITCODE"
```

当前已达到的效果：

- **CPU**：官方 56 条指令全部实现，周期数对齐 nesdev 官方表；非法指令会停下并显示在 CPU 窗口里。
  和 fogleman/nes 的差分测试 800 行完全一致（寄存器、标志位、周期数）。
- **PPU**：扫描线级渲染 —— 背景（名称表/属性表/图案表/调色板/滚动）、精灵（8x8 与 8x16、
  翻转、背景优先级、精灵 0 命中、精灵溢出）、VBlank 与 NMI 全部可用。
  两个测试 ROM 的画面都和 fogleman/nes **逐像素一致**；**《超级马里奥兄弟》从标题画面到 1-1
  也和参照实现逐像素一致**（第 34 / 60 / 300 / 900 / 1800 帧都核过）。
  （不做 dot 级流水线，取舍见 `godot-nes-implementation.md` §7.2。）
- **OAM DMA 的 CPU 停顿**：写 `$4014` 之后 CPU 停 513/514 个周期（PPU / APU 照常跑），和真机一致。
- **APU**：5 个通道（脉冲 ×2、三角波、噪声、DMC）+ 帧计数器 + 非线性混音，声音直接推给
  `AudioStreamGenerator` 播放。音频测试量到的频率和理论值相符（440.4 → 440.7 Hz）。
- **关闭 ROM**：菜单「文件 → 关闭 ROM」卸载卡带，回到空机状态（没装 ROM 时该项置灰）。
- **装载 ROM 后**：如果这个 ROM 会配 PPU 并开渲染，就能看到它画的画面；
  控制台也会打印 ROM 信息，例如 `Mapper 0 (NROM), PRG 16KB, CHR 8KB, 水平, NTSC`。
  **《超级马里奥兄弟》实测可以正常玩**：标题画面（含山丘上站着的马里奥）→ 按 Enter 开始 →
  World 1-1 开局、TIME 从 400 倒数。
- **CPU 窗口**：PC / SP / A / P / X / Y / CYC、8 个标志位（置位的亮绿）、栈顶 8 字节，
  以及是否撞到非法指令。
- **PPU 窗口**：CTRL / MASK / STATUS / OAMADDR / VRAM 地址 / 增量 / 背景表 / 精灵表、
  状态与开关、32 个调色板 RAM 色块（下方是颜色号）、两张 pattern table 预览。
- **ROM 信息窗口**：文件路径与大小、格式（iNES 1.0 / NES 2.0）、Mapper 与家族名、子 mapper、
  程序块与图案块的大小和 bank 数、WRAM/NVRAM、镜像、制式、平台、电池、trainer、
  向量表、CRC32、MD5，以及解析过程中的提示。
- **底部状态栏**：ROM 名、Mapper、FPS、帧号、运行状态。

想看一幅确定的画面，可以跑 PPU 测试 ROM（它会配好调色板/名称表/属性表/精灵并开渲染）：

```powershell
& "E:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe" `
  --path "E:\godot-nes\godot-nes" `
  -- --rom "E:\godot-nes\godot-nes\tests\roms\ppu-test.nes"
```

## 已知提示

- **`WARNING: 1 ObjectDB instance was leaked at exit`**：只在 `--headless`
  （音频驱动是 Dummy）运行时出现，`--verbose` 显示是 `AudioStreamGeneratorPlayback`。
  这是 Godot 内部对 audio playback 的引用计数，`Stop()` / `Dispose()` 都消不掉；
  带窗口正常运行不会出现，也不影响任何功能。
- **`ERROR: Window 0 spawned at invalid position`**：也只在 `--headless` 下出现，
  因为无头模式没有真实屏幕尺寸，`popup_centered()` 算不出合法位置。带窗口运行正常。
- **`ERROR: Could not create directory: 'user://logs'`**：在受限沙箱里运行时会看到，
  是 Godot 写不了 `%APPDATA%\Godot` 导致的，和本项目无关。

## 下一步

阶段 5：玩家 2 输入 + 用真实 ROM（超级马里奥兄弟）验证可玩性；然后是阶段 7 的 Mapper 扩展
（MMC2/MMC4、VRC 系列、MMC5 等，决定能玩多少游戏）。
计划和验收标准见 `godot-nes-emulator-plan.md`，各模块当前状态见 `godot-nes-implementation.md`。
