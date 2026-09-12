# godot-nes 功能模块实现文档

> **用途**：记录每个功能模块的**实现状态、对外接口、关键决策和验证方式**，作为后续开发的台账。
>
> **和其他文档的分工**
>
> | 文档 | 回答的问题 |
> |:---|:---|
> | `godot-nes-emulator-plan.md` | 要做什么（分阶段计划与验收标准） |
> | `godot-nes-ui-design.md` | 界面长什么样（主界面、调试窗口、语言分工） |
> | **本文** | **做到哪了**（已实现什么、怎么实现的、怎么验证的、还差什么） |
> | `godot-nes/README.md` | 怎么构建、怎么跑、键位 |
>
> **维护约定**
>
> 1. 每个模块按固定结构写：职责 → 文件 → 对外接口 → 关键决策 → 状态 → 验证 → 待办。
> 2. 状态标记：✅ 已实现可用 · 🟡 部分实现（能用但有明确缺口）· ⛔ 桩（只有接口/寄存器）。
> 3. 改代码就同步改本文；每完成一个计划阶段在 §13 变更记录里加一条。
> 4. 接口表只列对外可见的成员，内部细节放"关键决策"里。
> 5. 决策要写**为什么**，不然下次会有人把它改回去。

---

## 1. 总览

| # | 模块 | 位置 | 状态 | 计划阶段 | 一句话说明 |
|:---|:---|:---|:---|:---|:---|
| 1 | 核心接口 | `core/Interfaces/` | ✅ | 0 | IBus / IRenderer / IAudioSink / IInputSource 四个契约 |
| 2 | 卡带与映射器 | `core/Cartridge/` | ✅ | 0/7 | .nes 读取完整；mapper 0/1/2/3/4/7 全部实现并验证 |
| 3 | CPU 总线 | `core/Bus/Bus.cs` | ✅ | 2 | 内存映射、RAM 镜像、OAM DMA、手柄 |
| 4 | CPU 6502 | `core/CPU/` | ✅ | 1 | 官方 56 条指令 + 周期表，差分测试 800 行完全一致 |
| 5 | PPU 2C02 | `core/PPU/` | ✅ | 3/4 | 扫描线级渲染（背景+精灵+滚动+VBlank/NMI），差分测试画面逐像素一致 |
| 6 | APU 2A03 | `core/APU/` | ✅ | 6 | 5 个通道 + 帧计数器 + 非线性混音，音频测试核对到频率 |
| 7 | 手柄 | `core/Input/Controller.cs` | ✅ | 5 | 4021 选通/移位完整 |
| 8 | 机器组合体 | `core/NesConsole.cs` | ✅ | 0/3 | CPU/PPU 按 1:3 同步推进，NMI 在指令前递送 |
| 9 | 模拟器服务 | `autoload/EmulatorService.cs` | ✅ | 0 | 单例、ROM 装载、调试快照 |
| 10 | 调试数据枢纽 | `autoload/debug_hub.gd` | ✅ | UI | 按需拉快照并广播，无窗口时零开销 |
| 11 | 帧驱动 | `script/EmulatorCore.cs` | ✅ | 0 | 每帧唯一跨语言入口 + 命令行参数 |
| 12 | 显示 | `script/Display.cs` | ✅ | 0 | 帧缓冲 → 纹理 |
| 13 | 输入适配 | `script/InputAdapter.cs` | ✅ | 5 | Godot 输入 → 手柄位 |
| 14 | 音频输出 | `script/NesAudioPlayer.cs` | ✅ | 6 | AudioStreamGenerator → 播放；接在 `NesConsole.AudioSink` 上 |
| 15 | 图案预览 | `script/PatternTablePreview.cs` | ✅ | UI | 调试窗口里的 CHR 预览 |
| 16 | 主界面 | `ui/main.gd` | ✅ | UI | 菜单（含语言）、状态栏、快捷键、ROM 对话框 |
| 17 | 调试窗口 | `ui/debug_window.gd` + `ui/windows/` | ✅ | UI | CPU / PPU / ROM 信息三个独立窗口 |
| 18 | 主题 | `theme/dark_theme.tres` | ✅ | UI | 深色主题（资源文件 + `gui/theme/custom`，代码不碰样式） |
| 19 | 语言 | `lang/language.csv` + `autoload/ui_settings.gd` | ✅ | UI | 中英切换、跟随系统语言 |
| 20 | 测试 | `tests/` | ✅ | 全程 | 144 项冒烟断言 + 7 个合成 ROM + CPU/PPU 差分 + APU 频率核对 + mapper 自检/差分 |

---

## 2. 分层与跨模块约定

### 2.1 三层

```text
core/         纯 C#，不引用任何 Godot 类型；可单独编译/测试
script/       C# 节点，只做"把 Godot 和 core 接起来"这件事
autoload/     C# 单例（EmulatorService） + GDScript 单例（debug_hub）
ui/           GDScript，全部界面
```

依赖方向单一：`ui → autoload → script → core`，反向一律不允许。

### 2.2 约定 1：core 不认识 Godot

`core/` 里连一个 `using Godot;` 都没有。代价是数据要用 `byte[]` / 自定义枚举传递，
好处是核心可以直接搬到别的宿主（命令行工具、单元测试）而不动一行。

**验证方式**：`core/` 全目录 grep `using Godot` 应当为零。

### 2.3 约定 2：帧缓冲格式固定

RGBA8、行优先、从上到下，长度必须是 `256 * 240 * 4`，打包成 `byte[]`。
这样 Godot 侧可以直接 `Image.SetData(w, h, false, Rgba8, bytes)`，中间不做任何转换。

不透明像素的 alpha 必须是 `0xFF`：`Image` 是 RGBA，alpha=0 会让画面变透明（阶段 0 踩过这个坑）。

### 2.4 约定 3：每帧只跨一次语言，帧数据永不跨界

`ui/main.gd` 的 `_process` 只调一次 `EmulatorCore.StepAndPresent()`：
内部完成「采样输入 → 跑一帧 → 上传纹理」。
**帧缓冲 245 KB 绝不作为参数或返回值出现在跨语言调用里。**

### 2.5 约定 4：GDScript 能看到 C# 的什么（实测边界）

| C# 成员形态 | GDScript 可见性 |
|:---|:---|
| `public` 方法，参数/返回值是 Variant 兼容类型 | ✅ 可调 |
| `public` 方法，参数含 `ReadOnlySpan<T>` / 纯 C# 类型 | ❌ **静默不可见**（不进方法表） |
| `public` 属性，类型是 Variant 兼容类型 | ✅ 可读写（**不需要 `[Export]`**，且 **`private set` 挡不住写入**） |
| `public` 属性，类型是纯 C# 类 | ❌ 不进属性表（`get("Console")` 返回 null） |
| `[Signal]` 生成的 C# 事件 | ✅ 可 `connect`，参数正常传递 |
| 把 C# 节点标注成 `: Node` 再调它的成员 | ✅ 运行时正常（无自动补全而已） |

结论：要给 GDScript 用的数据一律做成 `Dictionary` / `Packed*Array`（C# 里就是 `int[]`、`byte[]`、`Color[]`、`string[]`）。

### 2.6 约定 5：桩代码必须能被发现

未实现的逻辑一律写 `TODO(阶段 N)：…`，并且**要在界面上看得见**。
例：CPU 跑到第一条指令就停下，把 opcode 记进 `NesConsole.UnimplementedOpcode`，
CPU 窗口和状态栏会显示「卡在未实现指令 $EA」。这样不会出现"看起来在跑其实什么都没做"。

### 2.7 约定 6：调试数据走快照，不走对象引用

因为 §2.5，GDScript 拿不到 `NesConsole`。所有调试数据由 `EmulatorService.GetXxxSnapshot()`
摊平成 `Dictionary` 交出去；GDScript 侧由 `DebugHub` 按 12 Hz 拉取并广播，
**没有任何调试窗口打开时一次都不拉**。

### 2.8 约定 7：失败必须给得出中文原因，而且不静默

所有解析/装载失败都返回或抛出带中文说明的错误，例如
`文件被截断：头里声明 PRG 32768 + CHR 8192 字节，实际只有 16384 字节（缺 24576 字节）。`
不允许"解析失败但什么都没说"。

### 2.9 代码风格

- C# 和 GDScript **都用制表符缩进**（见 `.editorconfig`）。
  本来 C# 想用 4 空格，但 Godot 编辑器在重新导入"被改动过"的脚本时会把它重排成制表符
  （只影响内容变过的文件，命令行 `--import` 和跑项目都复现不出来），
  跟它对着干只会反复产生无意义的 diff，所以统一跟它一致。
- 注释写"为什么这么做"，不写"这行在做什么"。
- 界面上的文字用中文。

---

## 3. 核心接口（`core/Interfaces/`）

**职责**：把核心和外部世界解耦的四个契约，明确"核心需要什么"。

| 文件 | 接口 | 成员 |
|:---|:---|:---|
| `IBus.cs` | `IBus` | `byte Read(ushort)` / `void Write(ushort, byte)` |
| `IRenderer.cs` | `IRenderer` | `int Width` / `int Height` / `byte[] FrameBuffer` / `bool TakeFrameReady()` |
| `IAudioSink.cs` | `IAudioSink` | `int SampleRate` / `void Write(ReadOnlySpan<float>)` |
| `IInputSource.cs` | `IInputSource` | `byte ReadButtons(int port)` |

**关键决策**

- `IRenderer.FrameBuffer` 是 `byte[]` 而不是 `Span<byte>`：Span 是 `ref struct`，
  不能做属性类型、不能跨 await，也不便于测试。
- `IAudioSink.Write` 收 `ReadOnlySpan<float>`：音频热路径要避免分配；
  代价是 `NesAudioPlayer.Write` 对 GDScript 不可见（正好也不需要它可见）。

**状态**：✅ 接口已定，`Ppu` 实现 `IRenderer`、`Controller` 实现 `IInputSource`、
`NesAudioPlayer` 实现 `IAudioSink`；`IBus` 由 `Bus` 实现。

**验证**：编译期即可（接口实现关系由编译器保证）。

**待办**：阶段 3 之后考虑给 `IRenderer` 加 `FrameNumber`，供显示端跳帧判断。

---

## 4. 卡带与映射器（`core/Cartridge/`）

**职责**：读 `.nes` 文件、拆出程序块（PRG）和图案块（CHR）、解析头部全部信息、提供 bank 映射。

| 文件 | 内容 |
|:---|:---|
| `Cartridge.cs` | iNES 1.0 / NES 2.0 解析，全部头部字段，校验和，向量表 |
| `Mapper.cs` | 映射器抽象基类 + 换 bank / 读 CHR 的公共工具 |
| `MapperFactory.cs` | mapper 号 → 映射器实例（0/1/2/3/4/7） |
| `MapperNames.cs` | mapper 号 → 家族名（只用于显示） |
| `Mappers/NromMapper.cs` | mapper 0 / NROM |
| `Mappers/Mmc1Mapper.cs` | mapper 1 / MMC1（SxROM） |
| `Mappers/UxromMapper.cs` | mapper 2 / UxROM（UNROM / UOROM） |
| `Mappers/CnromMapper.cs` | mapper 3 / CNROM |
| `Mappers/Mmc3Mapper.cs` | mapper 4 / MMC3（TxROM），含扫描线 IRQ |
| `Mappers/AxromMapper.cs` | mapper 7 / AxROM（AMROM / ANROM / AOROM） |

### 4.1 `Cartridge` 对外接口

| 分类 | 成员 |
|:---|:---|
| 常量 | `HeaderSize=16`、`TrainerSize=512`、`PrgBankSize=0x4000`、`ChrBankSize=0x2000` |
| 静态构造 | `FromFile(path)`、`TryFromFile(path, out cartridge, out error)`、`FromBytes(data, sourcePath="")` |
| 文件块 | `Header`、`Trainer`、`PrgRom`、`ChrRom`、`ChrRam` |
| 头部字段 | `IsNes20`、`MapperNumber`、`SubmapperNumber`、`MapperName`、`Mirroring`、`TvSystem`、`ConsoleType`、`ExtendedConsoleType`、`HasBattery`、`HasTrainer`、`HasFourScreen` |
| 大小 | `PrgSize`、`ChrSize`、`PrgBankCount`、`ChrBankCount`、`UsesChrRam`、`PrgRamSize`、`PrgNvRamSize`、`ChrRamSize`、`ChrNvRamSize`、`FileSize` |
| 派生 | `ResetVector`、`NmiVector`、`IrqVector`、`Crc32`、`Md5` |
| 其他 | `Mapper`、`SourcePath`、`Warnings`、`Describe()`、`DescribeDetailed()`、`MirroringText`、`TvSystemText`、`ConsoleTypeText` |

### 4.2 解析流程

```text
读文件 → 拷 16 字节头 → 清垃圾头 → 判 iNES 1.0 / NES 2.0
      → 算 mapper / 子 mapper / 镜像 / 各类 RAM 大小 / 制式 / 平台
      → 跳过 512 字节 trainer（如果有）
      → 切出 PRG（程序块）和 CHR（图案块）
      → 读向量表、算 CRC32 + MD5
      → 交给 MapperFactory 建映射器
```

### 4.3 关键决策

| 决策 | 为什么 |
|:---|:---|
| NES 2.0 的 mapper 高 4 位取自 **byte 8 低半字节** | 一开始按"byte 6 低半字节"猜是错的。FCEUX `ines.cpp:850`、jsnes `rom.js:176-179`、fogleman/nes 三份实现互相印证 |
| byte 6 低半字节在两种格式里都当**标志位**（镜像/电池/trainer/四屏） | 同上，jsnes 明确写了 "Flags from byte 6 (shared between iNES 1.0 and NES 2.0)" |
| 照抄 FCEUX 的垃圾头清理（`DiskDude!` / `demiforce` / `Ni03`） | 老写头工具会污染 byte 8-15，不清就会算错 mapper |
| iNES 1.0 的 byte 8-15 非零时只**警告不修正** | FCEUX 直接清零整段、jsnes 直接丢掉 byte 7 高半字节，两者都可能把合法的 mapper 16/32/64 改坏；这里选择提示而不是替用户改 |
| `byte4 = 0`（PRG 大小为 0）时按文件长度推断 | FCEUX 的做法是硬当 4 MB，越界风险大；按文件实际长度推断更稳，而且会写进 `Warnings` |
| CHR ROM 为 0 时分配 8 KB CHR RAM | iNES 1.0 没有 CHR RAM 大小字段，这是惯例。NES 2.0 理论上要显式写 byte 11，但真实 ROM 常常留 0，所以兜底 8 KB 并提示 |
| 支持 NES 2.0 的指数形式尺寸 `2^(b>>2) * ((b&3)*2+1)` | 少数大 ROM 用这种编码，公式与 FCEUX / jsnes 一致 |
| CRC32 + MD5 都算（PRG+CHR） | 和 FCEUX 报的是同一种，可以拿去对照 No-Intro / ROM 数据库 |
| 向量表从 PRG **尾部**读 | 上电时最后一块 16 KB PRG 总是映射在 `$C000-$FFFF`，所以 `$FFFA-$FFFF` 就是 PRG 数组最后 6 字节 |

### 4.4 映射器基类的接口（`Mapper.cs`）

数据通路分成两条：CPU 侧 `$8000-$FFFF`，PPU 侧 `$0000-$1FFF`（图案表）。

| 成员 | 用途 |
|:---|:---|
| `Name` | 家族名，只用于显示 |
| `CpuRead(addr)` / `CpuWrite(addr, v)` | PRG 窗口的读写（写就是换 bank 寄存器） |
| `PpuRead(addr)` / `PpuWrite(addr, v)` | CHR 窗口的读写 |
| `DynamicMirroring` | **映射器自己控制的镜像**，`null` = 用卡带头里的固定值（MMC1/MMC3/AxROM 会随时改） |
| `OnScanline()` + `IrqPending` | MMC3 的扫描线中断：PPU 每渲染完一条可见扫描线调一次，中断由 `NesConsole` 递送 |
| `Reset()` | 复位时把 bank / 镜像寄存器归零 |
| `PrgBank(bank, size)` / `ChrBank(bank, size)` | 把 bank 号折成字节偏移：支持负数（-1 = 最后一块），超范围按 bank 数取模 —— 卡带上的 bank 线接不满时真机就是这么绕回去的 |
| `ReadChr(offset)` / `WriteChr(offset, v)` | CHR ROM / CHR RAM 统一读写（写 CHR ROM 无效） |

### 4.5 六块映射器的规则与简化

| mapper | 家族 | 换 PRG | 换 CHR | 换镜像 | 中断 |
|:---|:---|:---|:---|:---|:---|
| 0 | NROM | 无 | 无 | 无 | 无 |
| 1 | MMC1 | 16KB ×2（4 种模式：32KB 块 / 固定头 / 固定尾） | 4KB 或 8KB | ✅ 控制寄存器低 2 位 | 无 |
| 2 | UxROM | $8000 可换 16KB，$C000 固定最后一块 | 固定 8KB | 无 | 无 |
| 3 | CNROM | 无（固定 16/32KB） | 8KB ×4 | 无 | 无 |
| 4 | MMC3 | 8KB ×4（两种模式：R6/R7 对调） | 1KB ×8（两种模式：两半对调） | ✅ $A000 | **扫描线 IRQ** |
| 7 | AxROM | 32KB ×16 | 无（板载 CHR RAM） | ✅ bit4 选单屏高低 | 无 |

**MMC1 的串行写**：CPU 一次只送 1 个 bit（写 `$8000-$FFFF` 的 bit0），连送 5 次才组成一个值，
**由第 5 次写落在哪个地址段决定写的是哪个寄存器**（`$8000` 控制 / `$A000` CHR0 / `$C000` CHR1 / `$E000` PRG）。
写的时候 bit7 = 1 是"复位移位寄存器"，顺带把 PRG 模式拉回 3（最后一块固定在 `$C000`）——
所有真游戏开头都有这一句，所以复位向量在游戏初始化之前就是可用的。

**MMC3 的寄存器**：`$8000` 先写寄存器号（顺便带 PRG/CHR 模式位），`$8001` 再写值；
地址的低位用 `address & 0xE001` 分成 8 个入口。

**已知简化**（写在明处，别当 bug）：

- MMC1：不做"连续两个 CPU 周期写会丢"的忽略规则；PRG RAM 写保护忽略；
  **512KB 卡带拿 CHR 寄存器高位当 PRG 高位（SXROM 扩展）没做**，超大 MMC1 卡带只能用到低 4 位对应的 bank；
- CNROM：不做**总线冲突**（真机写的时候数据总线上是"要写的值 AND ROM 那一字节"）；
- MMC3：IRQ 计数器按"每条可见扫描线一次"推进（真机是 A12 上升沿、dot 级）——
  这是扫描线级 PPU 的必然结果，做分屏够用；不区分 MMC3 rev A/B 的中断差异；
- 全体的 `$6000-$7FFF` WRAM 由 `Bus` 统一给固定 8KB，mapper 不参与（MMC5 那种要按声明大小分配的再说）。

**上电值取主流的惯例**（真机上这些寄存器是"不确定"的，取哪套只影响游戏初始化之前）：

- MMC1 控制寄存器 = `$1F`（PRG 模式 3、镜像水平、CHR 4KB 模式），跟 FCEUX 一致；
- MMC3 的 R0-R7 = `0,2,4,5,6,7,0,1`（也就是复位后 `$8000` = 第 0 块、`$A000` = 第 1 块），跟 FCEUX 一致；
- NROM / UxROM / CNROM / AxROM 的 bank 都取 0。

**Mapper 23（Konami VRC2b / VRC4e）**：8KB PRG 换 bank（`$8000`/`$A000`，`$C000`/`$E000` 固定），
8 个 1KB CHR bank 用**半字节**写（`$B000` 写 CHR0 低 4 位、`$B001` 写高 4 位，`$B002/$B003` 是 CHR1，
`$C000…` 依次到 CHR7），`$9000` bit0 选镜像（0=垂直、1=水平）。
这一族（VRC2/VRC4）各变体的差别**只在"哪根地址线选寄存器/半字节"**，所以照 FCEUX 的做法
先把地址按 `reg1mask=0x15`/`reg2mask=0x2a` 做一次 A0/A1 交换再统一解码。
VRC4 的 `$F000` 扫描线中断没做（Contra 是 VRC2 卡用不到）。
回归：`tests/roms/mapper23.nes` 自检 14/14；实测《Contra (Japan) (Sample)》标题与片头画面正确。

### 4.6 映射器怎么和机器接起来

| 接点 | 说明 |
|:---|:---|
| `NesConsole.LoadCartridge` | 把 `cartridge.Mapper` 交给 PPU（CHR 读写走它） |
| `Ppu.EffectiveMirroring` | `Mapper?.DynamicMirroring ?? Mirroring` —— 渲染取名称表和调试显示都用这个 |
| `Ppu.BeginScanline` | 渲染完一条可见扫描线、且**渲染开着**时调 `Mapper.OnScanline()`（真机没有 A12 上升沿时计数器不动） |
| `NesConsole.StepInstruction` | `Cartridge.Mapper.IrqPending` 和 APU 的帧 IRQ 一起接到 CPU 的 IRQ 线上 |
| `NesConsole.Reset` | 顺带调 `Mapper.Reset()`，让"复位"回到确定的初始状态 |

MMC3 的 IRQ 是**保持型**的：计数器减到 0 就把中断线拉住，直到程序写 `$E000` 才放开。
和真机一致（游戏必须自己关），所以中断处理程序里"先 `$E000` 关、重装、再 `$E001` 开"是标准写法。

### 4.7 状态

✅ **.nes 读取完整可用；mapper 0 / 1 / 2 / 3 / 4 / 7 全部实现并验证过。**
`MapperFactory` 里没有的 mapper 会抛 `NotSupportedException("暂不支持 Mapper 5（已实现：0, 1, 2, 3, 4, 7, 23）…")`，
装载时转成用户看得懂的提示。

### 4.8 验证

`tests/mapper_test.ps1`（新增）对 5 块 mapper 各跑两道关，细节见 §12.8：

- **自检**：`tests/make_mapper_test_rom.js` 生成的 ROM 里，程序真的去写换 bank 寄存器，
  再把每个窗口读回来的字节和**照着 nesdev 手算的期望值**比 —— 共 66 条断言，全过；
- **差分**：同样这几个 ROM 和 fogleman/nes 逐行比 CPU trace，**5 × 2000 行完全一致**；
- **MMC3 扫描线 IRQ**：跑 12 帧触发 28 次中断（理论 12 × 240 / 101 ≈ 28.5，容忍 ±2）。

### 4.9 待办

- [ ] MMC1 的 SXROM 扩展（512KB 以上卡带的 PRG 高位）。
- [ ] CNROM / UxROM 的总线冲突（BusConflict）—— 少数游戏和改版 ROM 会踩到。
- [ ] MMC3 rev A 与 rev B 的中断行为差异。
- [ ] 更多 mapper：MMC2/MMC4（PxROM，图案表按 A12 切）、VRC 系列、Namco 163、MMC5。
- [ ] NES 2.0 的 Vs. System / PlayChoice-10 目前只解析不处理（`ConsoleType` 已读出，但没有对应行为）。
- [ ] 卡带 WRAM 的**存储**现在在 `Bus` 里（固定 8 KB），`Cartridge` 只保留"头里声明多大"的数字。
      做到 MMC5 这种需要大 WRAM 的 mapper 时，要把存储挪到 `Cartridge` 并按声明大小分配。
      见 `Cartridge.cs` 里 `ChrRam` 声明下方的那段说明注释。
- [ ] 电池存档（`.sav`）读写还没做（计划阶段 8）。
- [x] ~~`HasFourScreen` 已解析，但四屏镜像需要额外 2 KB 视频 RAM，阶段 4 要确认与 `Mirroring.FourScreen` 的配合。~~
      → 已确认：PPU 的 `_vram` 本来就是 4 块 1KB（4KB），四屏直接可用；MMC1/MMC3 在四屏卡带上会忽略自己的镜像位。

---

## 5. CPU 总线（`core/Bus/Bus.cs`）

**职责**：CPU 地址空间的完整映射，以及 `$4014` OAM DMA。

| 成员 | 说明 |
|:---|:---|
| 常量 | `RamSize=0x800`、`PrgRamSize=0x2000` |
| 依赖 | `Ppu`、`Apu`（构造注入）、`Cartridge`（可空） |
| 设备 | `Controller1` / `Controller2` |
| 存储 | `Ram`、`PrgRam` |
| 操作 | `Read(ushort)`、`Write(ushort, byte)`、`Read16(ushort)` |

### 地址映射表

| 地址 | 行为 |
|:---|:---|
| `$0000-$1FFF` | 2 KB RAM，每 `$0800` 镜像一次 |
| `$2000-$3FFF` | PPU 寄存器，每 8 字节镜像一次 |
| `$4000-$4013` | APU 寄存器 |
| `$4014` | OAM DMA（只写） |
| `$4015` | APU 状态 |
| `$4016` / `$4017` | 手柄 1 / 2（写 `$4016` 同时选通两个手柄） |
| `$4020-$5FFF` | 扩展区，返回 0 / 忽略写 |
| `$6000-$7FFF` | 卡带 WRAM |
| `$8000-$FFFF` | 交给 `Cartridge.Mapper` |

### 关键决策

- **OAM DMA 用 `stackalloc byte[256]` 中转**：先把 CPU 页里 256 字节读出来，再 `Ppu.WriteOamDma`，
  避免 DMA 过程中访问 PPU 造成递归/副作用，也不产生堆分配。
- `$4016` 写会**同时**作用于两个手柄：真机就是这样（选通线是共用的）。

### 状态 / 待办

✅ 映射完整。
- [ ] 阶段 4：DMA 期间 CPU 要停 513/514 周期，现在没扣（`Bus.PerformOamDma` 上有 `TODO`）。
- [ ] 阶段 4：`$4014` 的 DMA 起点是 `OAMADDR`，写满一圈回绕 —— 已按这个实现，阶段 4 要复核。

---

## 6. CPU 6502（`core/CPU/`）

**职责**：6502 核心。**官方 56 条指令全部实现，周期数对齐 nesdev 官方表。**

| 文件 | 内容 |
|:---|:---|
| `Opcode.cs` | `AddrMode`（13 种寻址）、`Instruction`（56 条 + `Illegal`）、`Op` 记录 |
| `OpcodeTable.cs` | **生成的** 256 项 opcode 矩阵（操作 + 寻址模式 + 基础周期） |
| `Cpu.cs` | 寄存器、复位、取指-寻址-执行主循环、栈、标志位、中断、trace |
| `Cpu.Instructions.cs` | 56 条指令的实现（`Execute` 的大 switch） |
| `StatusFlags.cs` | P 寄存器位定义 |

### 6.1 对外接口

| 分类 | 成员 |
|:---|:---|
| 寄存器 | `A`、`X`、`Y`、`SP`、`P`、`PC`、`Cycles` |
| 状态 | `LastOpcode`、`HitIllegalOpcode`、`ResetVector` |
| 操作 | `Reset()`、`Step()`、`Nmi()`、`Irq()`、`GetFlag()`、`SetFlag()` |
| 静态 | `SizeOf(opcode)` —— 指令长度（BRK 特例见下） |
| trace | `FormatTrace()` —— 一行 CPU 状态，给差分测试用 |

### 6.2 执行流程

```text
Step()
├─ 撞过非法指令 → 直接返回 0（停住，不再往下跑）
├─ 取指：LastOpcode = Read(PC++)
├─ 查表：Op = OpcodeTable[opcode]
├─ 非法 → 置 HitIllegalOpcode，返回 2
├─ 寻址：ResolveAddress(mode) → 有效地址 + 是否跨页
├─ 执行：Execute(op, address) → 额外周期（只有分支会返回非 0）
├─ 跨页且是"读"类指令 → 额外周期 +1
└─ Cycles += 基础周期 + 额外周期
```

### 6.3 关键决策

| 决策 | 为什么 |
|:---|:---|
| **表驱动 + 两个 switch**，照搬 jsnes / fogleman-nes 的结构 | 指令表里没有重复逻辑；寻址模式只写一遍；56 条指令各一行，一眼能看完 |
| opcode 表用脚本从参照实现生成，不手抄 | 256 项手抄必错。生成脚本见下 |
| **指令长度不存表**，由寻址模式推 | 13 种模式对 3 种长度，比再存 256 字节划算 |
| 跨页附加周期**只给读类指令** | 写指令（STA）和读改写指令（ASL/INC）的基础周期里已经含了那一个周期，再给它们加就会多算。判据是 `HasPageCrossPenalty(ins)` |
| 分支的附加周期在 `Branch()` 里返回 1 或 2 | 分支是"跳转 +1、跨页再 +1"，用寻址阶段的跨页标志表达不了（那个标志会被判据过滤掉） |
| **BRK 的对外长度算 2 字节** | 它实际占 1 字节但会跳过补齐字节；反汇编和 trace 都按 2 字节才和 nestest 的日志一致。执行上仍是 `PC++` 后再压栈 |
| JMP 间接的**跨页 bug 照抄** | 指针低字节在 `$xxFF` 时高字节从同页回绕。不照抄的话个别游戏会跑飞 |
| 非法指令**停下并报告**，不当 NOP 混过去 | 非官方指令一个都没实现（计划阶段 7）。停下来能一眼看出某款游戏用到了哪条，而不是带着错误状态继续跑 |
| 非官方的 **NOP 变体按 NOP 处理** | `$1A`、`$04`、`$0C` 这些很多游戏在用，当 NOP 是无害且更兼容的 |
| 2A03 不看 D 标志位 | 它没有 BCD 模式；`CLD`/`SED` 照常翻标志位，但 `ADC`/`SBC` 不读 D |

### 6.4 opcode 表的来历

`OpcodeTable.cs` 是生成文件（文件头有说明），来源是 fogleman/nes 的
`instructionModes` / `instructionCycles` / `instructionNames` 三张表 ——
它们本身就是 6502 官方矩阵的 16×16 排布。生成脚本 `.ref/gen_opcode_table.js`
放在仓库外（`.ref/` 不入库），生成结果入库，方便 review。

**为什么要从参照实现生成**：手抄 256 项一定会错，而且错了很难发现（可能只是某个
不常用的寻址模式周期数差 1）。生成之后我又抽查了 `$00-$1F` 两行，和官方表一致。

### 6.5 状态

✅ **官方指令全实现、周期数经差分测试验证。**

- 非法指令：遇到就停，`NesConsole.IllegalOpcode` 会把这个 opcode 报到 CPU 窗口。
- `Nmi()` / `Irq()` 已实现压栈与跳转（各 7 周期），但**还没有东西去触发它们**
  —— 要等阶段 3 的 PPU VBlank。

### 6.6 验证

`tests/cpu_diff.ps1`：拿 fogleman/nes（Go）当参照，对 `torture.nes` 逐行比对
CPU 状态（PC/A/X/Y/P/SP/CYC），**800 行完全一致**。覆盖：

- 160 条官方非控制流指令各一遍；
- 索引寻址跨页（读类 +1 周期、写类不加）—— 这是最容易写错的地方；
- `(zp,X)` / `(zp),Y`；
- 分支四种情况（不跳 / 跳 / 跨页跳 / 反向循环）；
- JSR/RTS、PHA/PLA、PHP/PLP、TSX/TXS；
- BRK → IRQ 向量 → RTI 返回；
- JMP 自循环。

比字符串而不是比"看起来对不对"，所以寄存器差一位、标志位差一个、周期数差 1 都会被抓出来。

### 6.7 待办

- [ ] 阶段 1 剩余：`nestest.nes` 到手后跑一遍，作为官方日志的最终背书
      （差分测试已经覆盖了同样的东西，但 nestest 是社区公认的验收标准）。
- [x] ~~阶段 4：`$4014` OAM DMA 期间 CPU 要停 513/514 个周期，现在没扣。~~
      → 已实现（`Bus.OamDmaStallPending` + `Cpu.Step()` 里把停顿周期算进返回值，
      PPU / APU 照常推进）。SMB 的时序对不上有一部分就是它。
- [ ] 阶段 7：非官方指令（SLO/LAX/RLA…），很多日版游戏会用到。
- [ ] 基准测试：目前 600 帧（约 900 万条指令）连启动带装载 5.3 秒，远没到瓶颈；
      等 PPU 接上之后再测真实帧预算。

---

## 7. PPU（`core/PPU/`）

**职责**：2C02。寄存器、视频内存、时序（扫描线级）、背景 + 精灵渲染、VBlank/NMI 全部实现。

| 文件 | 内容 |
|:---|:---|
| `Ppu.cs` | 寄存器、VRAM/调色板访问、OAM、扫描线时序、索引帧 → RGBA |
| `Ppu.Rendering.cs` | 每条可见扫描线的背景 + 精灵渲染 |
| `NesPalette.cs` | 64 色调色板（RGBA8） |

| 分类 | 成员 |
|:---|:---|
| 常量 | `ScreenWidth=256`、`ScreenHeight=240`、`ScanlineCount=262`、`DotsPerScanline=341`、`CyclesPerFrame=89342`、`OamSize=256`、`PaletteSize=32` |
| 寄存器 | `Ctrl`、`Mask`、`Status`、`OamAddr`、`OamData` |
| 状态解读 | `NmiEnabled`、`RenderingEnabled`、`SpriteSize8x16`、`BackgroundPatternBase`、`SpritePatternBase`、`Increment`、`VramAddress` |
| 存储 | `Vram`、`PaletteRam`、`Oam` |
| 时序 | `Scanline`、`Dot`、`FrameCount`、`Step(ppuCycles)`、`TakeNmiRequest()`、`Reset()` |
| 输出 | `IndexBuffer`（6 位调色板索引）、`FrameBuffer`（RGBA8）、`TakeFrameReady()` |
| 操作 | `ReadRegister()`、`WriteRegister()`、`ReadVram()`、`WriteVram()`、`WriteOamDma()` |
| 注入 | `Mirroring`（卡带头里的固定值）、`EffectiveMirroring`（考虑 mapper 动态改的那个）、`Mapper`（读 CHR 用） |

### 7.1 关键决策

| 决策 | 为什么 |
|:---|:---|
| **按扫描线渲染，不做 dot 级流水线** | 真机是每 dot 取图块 + 移位寄存器，要完整模拟 Loopy 寄存器和取指相位，代码量翻几倍。改成"进入一条可见扫描线时，用当前滚动寄存器一次算完这一行"，整个渲染部分只有 ~180 行 |
| 先渲染到 **6 位索引缓冲**，进 VBlank 时才转 RGBA | 和硬件一致（PPU 输出的是调色板索引，颜色是 DAC 的事）；顺带让差分测试可以逐字节比索引，不受调色板表示差异影响 |
| 每帧只在扫描线边界上做事件 | `Step(ppuCycles)` 里是"跨过几条扫描线"的循环，不是 dot 循环 —— 每帧省掉 8.9 万次空转 |
| VBlank 在扫描线 241 置位，pre-render（261）清标志 | 和真机一致；NMI 用 `TakeNmiRequest()` 交给 `NesConsole` 在指令前递送 |
| 属性表的移位量 = `((coarseRow & 2) << 1) + (coarseCol & 2)` | 属性字节 4 个 2 位字段分别是左上/右上/左下/右下。**这里写错过一次**（列方向多左移了一位），被差分测试抓出来 |
| 写 `$2000` 时把低 2 位拷进 `t` 的 bit10-11 | 真机行为，游戏靠它切换名称表。少了这一句"滚到另一块名称表"就失效 |
| 精灵 Y 按 `OAM + 1` 算，8x16 用 bit0 选图案表 | 真机语义；8x16 的图块号高 7 位选图块 |
| **精灵 0 命中与优先级无关** | 只要精灵 0 的不透明像素压在背景的不透明像素上就置位，优先级只决定"画谁的颜色"。**这里写错过一次**：先判优先级（`behindBackground` 就 `continue`）再判命中，于是带"在背景后面"属性的精灵 0 永远不置位 —— SMB 的标题画面正好死等这个标志，游戏直接卡死（见 §13 那次排查） |
| `$2002` 的低 5 位给"开放总线"值 | 真机返回 PPU 数据总线上残留的值（最后一次写进来的寄存器值）。少了它 SMB 开机那段 `LDA $2002 / BPL` 的 A 和 Z 标志就和真机不一样 |
| **纵向卷轴基准分两种（写入值是否为 0）** | 场景分屏（`$2005 = X,$22`，纵向 34）要按**写入行**做基准，底栏分屏（`$2005 = $00,$00`）要按**帧顶**做基准。两条各有一个客观验证：前者对应"与参照实现逐像素 0 差异"，后者对应"LEVEL/EXP 落在屏幕 208-215 行、与 FCEUX 一致"。**这里连着写错过两次**，详见 §16 |
| `$4014` OAM DMA 要停 513/514 个周期 | 真机行为；停顿期间 CPU 不取指，但 PPU / APU 照常跑。少了它整机时序会整体偏 |

### 7.2 已知的简化（写在明处，别当 bug）

- 同一扫描线**行内**改 `$2005/$2006/$2000` 只影响下一行，不是下一 dot；
- 精灵 0 命中的**时机**是行级（"这一行里有没有命中"和真机一致，但精确到哪个 dot 不是）；
- 精灵溢出标志同样是行级；
- 没有模拟 `v` 在行内自增，帧末也不把 `t` 拷回 `v`；
- 没做 PPUMASK 的色彩强调（emphasis）位，只做了灰度位。

对绝大多数游戏够用（含 SMB 那种靠精灵 0 命中分屏的状态栏）；做彩虹效应、曼哈顿计划
那类靠行内时序的 demo 就得换成 dot 级。

### 7.3 状态

✅ 背景、精灵（8x8 / 8x16、翻转、优先级、精灵 0 命中、精灵溢出）、滚动、VBlank/NMI 都可用。
和 fogleman/nes 逐像素一致，并且《超级马里奥兄弟》从标题画面到 1-1 的实际画面也逐像素一致。

### 7.4 验证

`tests/ppu_diff.ps1`：

- 两个测试 ROM（`ppu-test.nes` 滚动 0,0；`ppu-scroll.nes` 滚动 3,5 + 垂直镜像）和 fogleman/nes 的画面
  **逐像素一致（61440 字节全同）**，另有手算核对的关键像素和 NMI 计数；
- `ppu-sprite0.nes`：精灵 0 带"在背景后面"属性、压在整屏不透明的背景上，ROM 自己轮询 `$2002` 的 bit6，
  命中写 `$0300 = $A5`。**这块就是给上面那个 bug 加的回归测试**（把命中判定挪回优先级之后就写 0）。

细节见 §12.7 / §12.8。

### 7.5 待办

- [ ] 精灵 0 命中的 dot 级时机（现在按行）—— 如果某游戏的分屏抖动，先怀疑这里。
- [ ] PPUMASK 的色彩强调位。
- [ ] `$2002` 读之后的 VBlank 抑制（真机读一次会"吃掉"这次 VBlank 的 NMI；同样地，
      在标志刚置位那一两个 dot 内读 $2002 会抑制 NMI）。
- [ ] 开放总线（open bus）：现在只做了 `$2002` 的低 5 位，未映射地址的读还是返回 0。

---

## 8. APU（`core/APU/`）

**职责**：2A03 的 5 个通道（脉冲 ×2、三角波、噪声、DMC）+ 帧计数器 + 非线性混音。

| 文件 | 内容 |
|:---|:---|
| `Apu.cs` | 寄存器、帧计数器、时序推进、采样与混音、样本缓冲 |
| `ApuChannels.cs` | `LengthTable`、`Envelope`、4 种通道 |

| 分类 | 成员 |
|:---|:---|
| 常量 | `SampleRate=44100`、`CpuFrequency=1789773` |
| 通道 | `Pulse1`、`Pulse2`、`Triangle`、`Noise`、`Dmc` |
| 时序 | `Step(cpuCycles)`、`Reset()` |
| 寄存器 | `ReadStatus()`、`WriteStatus()`、`WriteRegister()`、`WriteFrameCounter()` |
| 输出 | `DrainSamples(Span<float>)`、`FrameIrqPending` |
| 注入 | `Bus`（DMC 读采样数据用） |

### 8.1 关键决策

| 决策 | 为什么 |
|:---|:---|
| **按 CPU 周期推进**，由 `NesConsole` 和 PPU 一起同步驱动 | 采样点才能落在正确的时刻；不需要"先跑完一帧再一次性生成一帧的音频"这种别扭结构 |
| 采样用**整数累加器**（每周期加 44100，超过 1789773 就吐一个样本） | 浮点累加跑久了会漂；整数不会，而且每周期只有一次加法和比较 |
| **每个 CPU 周期把各通道输出累加，出样本时取平均**（盒式滤波） | 44.1kHz 直接点采样方波会严重混叠，折回来的频率和乐音无关，听感就是"发毛、有噪音"。取一个采样周期内的平均等效于一次低通 —— jsnes 用的就是这套做法 |
| 混音用 NES 那套非线性公式 | 线性的听起来会明显不对；公式天然保证**全静音时输出精确为 0**（两路都是 0） |
| 输出再走一次**直流消除器**（一阶高通，反馈 1/1024） | 真机 DAC 是单极性的（0 到满量程），照原样播出去等于带着恒定直流：吃动态范围、开机还会"咚"一下。jsnes 同样带这个滤波器 |
| 每个通道一个类，包络抽出来共用 | 脉冲和噪声的包络完全一样，共用一份；三角波和 DMC 的结构差异大，各自独立反而更清楚 |
| 脉冲 1/2 用同一个类的两个实例，只靠 `IsSecondPulse` 区分 | 两者唯一的差别是扫频取反时是"减变化量"还是"减变化量+1" |
| `Apu.Reset()` 直接**重建通道对象** | 复位是低频操作，重建比给每个通道写一遍清空代码更省事、更不容易漏字段 |
| 帧计数器用 `switch` 匹配周期阈值 | 4 步 / 5 步模式的时钟点就是一张固定的周期表，写成 switch 一眼能对上 nesdev 的表 |
| **长度计数器只在通道使能时才装载**（$4003/$4007/$400B/$400F） | 真机就是这么规定的，写这几条寄存器时通道被禁用则不装载长度；jsnes 也这么做 |
| 三角波**不活动时定序器停住、输出电平保持** | 真机 DAC 是"保持"而不是归零；归零会让每个音符结束时电平跳变，低音声部一下一下地"咔" |
| DMC **没使能时整个定时器都不走**，样本放完也不再重新武装移位器 | 这是最难查的一个：漏掉之后，$4015 关掉 DMC 后残留的移位器会拿着最后一个字节反复"按位加 2/减 2"，DAC 以 DMC 速率一直抖 —— 听感就是**持续的背景嘶声**。fogleman/jsnes 都在定时器入口直接 return，FCEUX 把 `DMCSize` 清 0；回归 ROM `apu-dmc-off.nes` 专门盯这个 |
| DMC 没实现**偷 CPU 周期** | 对声音没影响，只是时序上不如真机精确；写进待办，不假装做了 |

### 8.2 状态

✅ 五个通道、帧计数器（含 IRQ）、非线性混音、抗混叠采样、直流消除全部可用。

### 8.3 验证

`tests/apu_test.ps1`：音频没有"逐字节可比"的参照（各家混音单位和滤波都不同），
所以改成核对**物理量**：

| ROM | 判据 | 实测 |
|:---|:---|:---|
| `apu-silent.nes` | 输出**精确为 0** | 0 ✓（顺便证明混音+直流消除之后没有偏移） |
| `apu-pulse.nes`（440.4 Hz 方波） | 频率在 ±2% 内 | 440.7 Hz ✓，峰值 4891 |
| `apu-triangle.nes`（220.2 Hz） | 频率在 ±2% 内 | 220.9 Hz ✓，峰值 7608 |
| `apu-noise.nes` | 有声音且过零点够多（宽带） | 峰值 5711、过零 41814 ✓ |
| `apu-dmc-off.nes` | 开头 DMC 真播了、`$4015` 关掉之后尾段必须安静 | 开头峰值 14904、尾段峰值 **0** ✓ |

频率是数"中值过零点"数出来的（输出是单极性的，拿 0 当阈值会一个都数不到 —— 踩过）。

> 这几个 ROM 里 `$4015` 必须在写长度寄存器**之前**：通道没使能时写 $4003/$400B/$400F
> 是不装载长度计数器的（真机行为），先写长度就会得到一片静音 —— 踩过。

真实 ROM 的对照办法（没有逐字节参照时怎么判断"声音对不对"）：
`--dump-audio` 导出 PCM，和 jsnes 的导出比**过零率**（音高）而不是比波形 ——
SMB 实测过零率相关系数 **0.959、最佳滞后 0**，即同一段旋律、同一时刻。
响度包络的相关系数只有 0.68 左右，那是因为两家混音曲线、立体声权重都不同，属于正常差异。

### 8.4 待办

- [ ] DMC 对 CPU 的周期偷取（4 个周期/字节）。
- [ ] 帧计数器的 IRQ 时序：现在是"到点置标志"，真机有 1-2 个周期的延迟。
- [ ] 三角波的真机阶梯是"每级保持一段时间"，没有像 jsnes 那样对三角波单独做斜坡插值。
- [ ] 只有盒式滤波（一个采样周期内取平均），没有更高阶的低通；真机输出比现在更"糊"一点。

---

## 9. 手柄（`core/Input/Controller.cs`）

**职责**：标准手柄的 4021 移位寄存器。

| 成员 | 说明 |
|:---|:---|
| `NesButton` 枚举 | A=0、B=1、Select=2、Start=3、Up=4、Down=5、Left=6、Right=7（就是移位顺序） |
| `Buttons` | 按键位掩码，由 Godot 侧每帧写入 |
| `Strobe` | 选通状态 |
| 操作 | `SetButton()`、`IsPressed()`、`Write(byte)`、`Read()`、`ReadButtons(int)` |

### 关键决策

- 枚举值**直接等于位序**，`SetButton` 里 `1 << (int)button` 一步到位，不用查表。
- `Read()` 里高位补 1：读完 8 位后一直返回 1，这是真机行为，有些游戏靠它判断手柄存在。
- 实现 `IInputSource`，但 Godot 侧走的是直接写 `Buttons`（每帧一次），
  而不是让游戏每读一次就跨语言一次。
- 两个手柄都有映射：手柄 1 = 方向键/WASD/`X`·`K`/`Z`·`J`/Enter/Backspace + **手柄设备 0**；
  手柄 2 = **小键盘 8·2·4·6 / 1 / 3 / + / 0** + **手柄设备 1**。
  手柄 1 的 joypad 事件特意从"任意设备"（device=-1）收窄成 device=0，
  否则插着第二个手柄时，2P 的手柄会同时操控 1P。

### 状态

✅ 完整。

### 待办

- [ ] 连发/自动连发（可选，阶段 8）。

---

## 10. 机器组合体（`core/NesConsole.cs`）

**职责**：把 CPU / PPU / APU / Bus / 卡带装成一台机器，并提供帧调度。

| 成员 | 说明 |
|:---|:---|
| 常量 | `CpuCyclesPerFrame=29780`（NTSC）、`PpuCyclesPerFrame=89342`、`PpuCyclesPerCpuCycle=3` |
| 组件 | `Bus`、`Cpu`、`Ppu`、`Apu`、`Cartridge` |
| 状态 | `FrameCount`、`Paused`、`IllegalOpcode`、`FrameBuffer` |
| 操作 | `LoadCartridge()`、`Reset()`、`StepFrame()`、`StepInstruction()` |

> 阶段 0 的 `core/Diagnostics/DiagnosticRenderer.cs`（调色板色块 + 棋盘格占位画面）
> **已经删除** —— PPU 现在自己会画，占位渲染器连同 `RenderPlaceholderFrame()` 一起清掉了。

**关键决策**

- `LoadCartridge` 会把 `Mapper` 和 `Mirroring` 注入 PPU —— 因为 PPU 读 CHR 要靠 mapper。
- `StepFrame` 按"跑满 29780 个 CPU 周期"推进：每执行一条指令，PPU 就同步跑 `周期数 × 3`，
  这样 VBlank 和 NMI 会落在正确的时刻，而不是"一帧跑完再补一个 NMI"。
- NMI 在 `StepInstruction` 里**先于指令**递送（`Ppu.TakeNmiRequest()`），让 CPU 先跳进中断处理程序。
- 撞到**非法指令**就**跳出并且不再推进**（CPU 自己也会停），把 opcode 记进 `IllegalOpcode`
  给 CPU 窗口显示（见 §2.6）。

**待办**

- [ ] `$4014` DMA 的 CPU 停顿（513/514 周期）要在这里扣。
- [ ] PAL 制式的帧周期数（现在是 NTSC 的 29780 写死）。

---

## 11. Godot 层（C#）

### 11.1 `autoload/EmulatorService.cs` ✅

全局单例。**唯一持有 `NesConsole` 的地方**，也是 GDScript 访问核心的唯一入口。

| 分类 | 成员 |
|:---|:---|
| 单例 | `Instance`、`Console` |
| 状态（可见给 GDScript） | `CurrentRomPath`、`CurrentRomDescription`、`LastError` |
| 信号 | `RomLoaded(path, description)`、`RomLoadFailed(path, error)`、`RomClosed()` |
| 操作 | `LoadRom(path)`、`CloseRom()`、`Reset()`、`TogglePause()`、`StepInstruction()` |
| 路径 | `static ToFileSystemPath(path)`（处理 `res://` / `user://`） |
| 快照 | `GetSystemSnapshot()`、`GetCpuSnapshot()`、`GetPpuSnapshot()`、`GetRomInfo()`、`GetDebugSnapshot()` |
| 调色板 | `GetPaletteRam()` → `int[]`、`GetNesPalette()` → `Color[]` |
| mapper | `GetImplementedMappers()` → `int[]`（UI 用它判断"能不能跑"） |

`GetPpuSnapshot()` 里和 mapper 有关的三个键：`mirroring` 给的是**当前生效**的镜像
（`Cartridge.DescribeMirroring(ppu.EffectiveMirroring)`，MMC1/MMC3/AxROM 游戏运行时会变），
`mapper` 是家族名，`mapper_irq` 是挂起状态（调 MMC3 分屏时有用）。

**关键决策**

- 属性 `CurrentRomPath` 等是**故意公开**的：不写 `[Export]` 只要类型 Variant 兼容，
  GDScript 也能读写。注意 C# 的 `private set` **拦不住** GDScript 写入（实测），
  所以只读状态一律用 `GetXxxSnapshot()` 方法暴露。
- `Console` 属性是 `NesConsole`（纯 C# 类），对 GDScript **不可见** —— 这是设计意图，不是 bug。
- 快照方法只在有调试窗口时被调用（由 `DebugHub` 控制频率），所以不必抠分配。

**待办**

- [ ] 存档 / 设置 / 最近打开的 ROM 列表。
- [ ] 阶段 6：把 `Apu.SampleInto` 的结果推给 `NesAudioPlayer`。

### 11.2 `script/EmulatorCore.cs` ✅

热路径节点，**GDScript 与 C# 之间唯一的每帧入口**。

| 成员 | 说明 |
|:---|:---|
| `StepAndPresent()` | 采样输入 → `StepFrame()` → 需要时 `Display.Present()` → 处理 `--dump-frame` |
| `SaveFramePng(path)` | 把当前帧写成 PNG，返回空字符串表示成功 |

命令行参数（写在 `--` 之后）：

| 参数 | 说明 |
|:---|:---|
| `--rom <路径>` / `-r <路径>` | 启动时装载（也可以直接给一个 `.nes` 路径） |
| `--rom-info` | 只解析并打印 ROM 头信息然后退出；失败退出码 1 |
| `--dump-frame <png>` | 跑若干帧后写 PNG 并退出 |
| `--dump-frame-after <帧数>` | 配合上一个用，默认第 1 帧 |
| `--press <键> <按下帧> [松开帧]` | 无头测试钩子：从第 N 帧起按住手柄 1 的某个键（可给松开帧），可重复 |

**关键决策**

- 输入采样放在 `StepAndPresent` **内部**而不是让 GDScript 调：
  `InputAdapter.PushTo` 的参数是 `NesConsole`，GDScript 根本调不到（§2.5），正好强制它留在 C# 里。
- **由主场景的 `_physics_process` 驱动（固定步长），不跟显示刷新率走**：
  物理步长是常量（1/60 秒），节奏不会被 165Hz 显示器带跑；渲染卡一下也只影响画面、不影响模拟节奏。
  实测带窗口时三种配置都是 60.0~60.2 帧/秒、0 次 >50ms 的卡顿（用 `_process` 时同机测到过
  30~45 帧/秒和每秒一次的 500ms 停顿 —— 那是 Godot 主循环被渲染拖住，物理步不会）。
- 物理步长（1/60 = 16.667ms）和 NTSC 帧长（1/60.0988 = 16.639ms）**不相等**，所以内部还是按时间累积：
  每步加 1/60 秒、够 16.639ms 才跑一帧 —— 绝大多数步跑 1 帧，大约每 607 步多跑 1 帧，
  长期平均正好贴着真机（硬跑一帧一步会慢 0.16%）。
- 无头（`--headless`）**故意不节流**：一次调用跑一帧，并顺手把 `Engine.PhysicsTicksPerSecond`
  提到 1000，否则 Godot 的物理步按真实时间走，导 1800 帧要等 30 秒。
- 节点用 `%Display` / `%InputAdapter` 场景唯一名查找，不写死节点路径。

**待办**

- [ ] 阶段 5：快进（跑多帧不渲染）、倒带。
- [ ] 阶段 8：`--dump-frame` 升级成可指定帧区间的批量导出，方便对比 FCEUX。

### 11.3 `script/Display.cs` ✅

256×240 帧缓冲 → `Image` → `ImageTexture` → `TextureRect`。

**关键决策**

- **复用同一个 `Image` 和 `ImageTexture`**：每帧只 `SetData` + `Update`，零分配。
- 固定 `TextureFilter.Nearest` + `KeepAspectCentered` + `IgnoreSize`：像素不糊、等比居中。
- `Image.SetData` 在 Godot 4.7 的签名是 `(int w, int h, bool mipmaps, Image.Format, byte[])`，
  没有单参重载（踩过，编译报 CS1501）。

### 11.4 `script/InputAdapter.cs` ✅

`PushTo(NesConsole)` 把 Godot 的 `Input.IsActionPressed` 翻译成手柄位。
映射表是静态数组（`(action, button)` 元组），一组循环搞定，不分配。

键盘/手柄映射定义在 `project.godot` 的 `[input]` 段（共 18 个动作，见 §12.4）。

### 11.5 `script/NesAudioPlayer.cs` ✅

`AudioStreamPlayer` + `AudioStreamGenerator`，实现 `IAudioSink`。

**关键决策**

- 缓冲不够时**整批丢弃**而不是排队：宁可丢一小段，也不让延迟越积越大。
- `_ExitTree` 里 `Stop()` 并松开 `_playback` 引用。注意：无头（Dummy 音频驱动）退出时
  Godot 仍会报一条 `ObjectDB instance was leaked`，属于 Godot 内部对 audio playback 的计数，
  `Stop()`/`Dispose()` 都消不掉，带窗口正常运行不会出现。
- `Write(ReadOnlySpan<float>)` 对 GDScript 不可见（§2.5），这是预期行为。

**待办**：DMC 和重音比较多的曲子上要留意缓冲欠载；现在的做法是"装不下就整批丢"。

### 11.6 `script/PatternTablePreview.cs` ✅

PPU 窗口里的 CHR 预览控件（`TextureRect` + `[Export] int TableIndex`）。

**关键决策**

- 故意做成 **C# 节点**：CHR 解码属于"重数据"，放 C# 侧读就不用把字节数组跨语言传。
  GDScript 只负责摆位置，然后调无参的 `Refresh()`。
- 解码用 4 级灰阶（调色板 `$0F/$00/$10/$30`），因为只看图案形状，不看颜色。
- GDScript 侧把刷新节流到 500 ms（12 Hz 太快，解码 256 个图块没必要）。

---

## 12. 界面层（GDScript）与测试

### 12.1 `autoload/debug_hub.gd` ✅

调试数据的节流阀。

| 成员 | 说明 |
|:---|:---|
| 常量 | `PULL_INTERVAL = 1/12` 秒 |
| 数据 | `snapshot`（含 `system` / `cpu` / `ppu` / `rom` 四段）、`rom`（缓存） |
| 信号 | `snapshot_updated(snapshot)`、`rom_updated(rom)` |
| 操作 | `register_consumer()`、`unregister_consumer()`、`consumer_count()`、`pull_now()`、`service()` |

**关键决策**

- **消费者计数为 0 时 `_process` 直接 return**：没有调试窗口打开时，调试开销精确为 0。
- ROM 信息不跟着 12 Hz 走，而是监听 C# 的 `RomLoaded` 信号后重拉一次并缓存进快照。
  因为它在两次装载之间不会变。

### 12.2 `ui/debug_window.gd`（`class_name DebugWindow`）✅

所有调试窗口的基类。

- `close_requested` → `hide()`：**关闭即隐藏，不 free**，窗口位置和状态都留着。
- 可见 → `register_consumer()` + 立刻 `pull_now()` 刷一次；隐藏 → `unregister_consumer()`。
- 不可见时收到快照也不刷新。
- **跟随主窗口的界面缩放**（`_follow_main_window_scale`）：见 §12.5 最后一段。
- 子类只需覆写 `_refresh(snapshot: Dictionary)`，并在自己的 `_ready` 里先建好界面再 `super()`。

### 12.3 三个调试窗口 ✅

| 窗口 | 快捷键 | 设计尺寸 | 内容 | 界面构建方式 |
|:---|:---|:---|:---|:---|
| `cpu_window` | F2 | 430×340 | PC/SP/A/P/X/Y/CYC、8 个标志位（置位亮绿）、栈顶 8 字节、未实现指令提示 | 骨架在 tscn，脚本只填 text |
| `ppu_window` | F3 | 440×580 | CTRL/MASK/STATUS/OAMADDR/VRAM/增量/背景表/精灵表、状态开关、32 个调色板色块、两张 pattern table 预览 | 寄存器在 tscn；32 个色块用代码建 |
| `rom_window` | F4 | 540×500 | 17 行 ROM 信息（见 §4.1）+ 解析提示 | tscn 只留容器，行按固定表用代码建一次 |

**关键决策**

- `rom_window` 的行**只建一次**，之后只改 `text`：否则 12 Hz 反复 new 34 个 Label 会很浪费。
- 窗口内要把"取数/格式化"和"显示"分开，这样无头环境下也能验证数据链路。

### 12.4 `ui/main.gd` + `scene/main.tscn` ✅

| 节点 | 说明 |
|:---|:---|
| `Main` | 场景根，挂 `main.gd` |
| `Ui/Root/MenuBar` | 顶部菜单（6 项，用 `MenuBar` + `PopupMenu`，标题就是子节点顺序） |
| `Ui/Root/Display` | `%Display`，全屏等比居中 |
| `Ui/Root/StatusBar` | 底部一行：ROM 名 / Mapper / FPS / 帧号 / 状态 |
| `DebugWindows/{Cpu,Ppu,Rom}Window` | 三个独立 `Window` |
| `EmulatorCore` / `InputAdapter` / `NesAudioPlayer` | C# 节点 |
| `RomFileDialog` | 打开 ROM |

**菜单与快捷键**

| 菜单 | 条目 |
|:---|:---|
| 文件 | 打开 ROM… / 关闭 ROM（没装 ROM 时置灰） / 退出 |
| 模拟 | 复位 / 暂停继续 / 单步一条指令 |
| 视图 | CPU 窗口(F2) / PPU 窗口(F3) / ROM 信息窗口(F4) / 全屏(F11) ← 带勾选状态 |
| 调试 | 显示状态栏(F1) / 当前帧存成 PNG |
| 语言 | 中文 / English ← 单选项，勾当前语言（见 §12.6） |
| 帮助 | 快捷键 / 关于 |

**关键决策**

- **快捷键统一走 `project.godot [input]` 动作 + `_unhandled_input`**，
  不用 `PopupMenu.set_item_shortcut`。两套机制并存会重复触发，而且菜单 `Shortcut` 与
  `[input]` 动作的优先级本来就需要额外验证；菜单标题里直接写 "(F2)" 提示即可。
- 18 个输入动作：`nes_up/down/left/right/a/b/start/select`（键盘 + 手柄双映射）、
  `emulator_load_rom(O)`、`emulator_reset(R)`、`emulator_pause(P)`、`emulator_step_frame(F10)`、
  `emulator_toggle_debug(F1)`、`emulator_window_cpu(F2)`、`emulator_window_ppu(F3)`、
  `emulator_window_rom(F4)`、`emulator_fullscreen(F11)`、`emulator_quit(Esc)`。
- `Esc` 只走 `_unhandled_input`：这样 FileDialog 打开时 Esc 先被对话框吃掉，关掉对话框后才轮到退出。
- `MenuBar.prefer_global_menu = false`：否则 macOS 上菜单会跑到系统菜单栏，两端外观差很多。
- C# autoload / C# 节点照常标注 `: Node`（实测运行时可以正常调用，只是没有自动补全）。

### 12.5 深色主题（`theme/dark_theme.tres`）✅

**样式全部在资源文件里，代码不碰样式**：`theme/dark_theme.tres` 是一份普通的 Godot `Theme` 资源，
用 `project.godot` 的 **`gui/theme/custom`** 挂在项目主题上。改配色/字号/圆角直接开 Godot 的主题编辑器改这个文件。

这样做的两个好处：

1. **一处生效，处处生效**：项目主题是 Godot 主题查找链的全局兜底，主窗口、每个独立 `Window`、
   运行中 new 出来的 `AcceptDialog` / `FileDialog` 里的控件全都吃得到 ——
   不需要任何"把主题挂到某个节点上"的代码（早期版本就是这么干的，见下面那段坑）。
2. 样式不再是代码，就没有"改一个样式要改 `.gd`、还要担心和 tscn 里的 `theme_override_*` 打架"的问题。

> **早期版本是把 `Theme` 用代码建出来的**（`autoload/ui_theme.gd` 里的 `_build_theme()`），
> 生成之后再用 `apply_to(node)` 一个个挂到 `$Ui/Root` 和每个 `Window` 上。
> 之所以那么绕，是因为踩到了"`get_tree().root.theme` **不会**传给场景里的控件"这个坑
> （挂上去之后 `Label.get_theme_font_size()` 还是默认的 16，菜单栏拿到的还是默认样式盒；
> 挂在 `Control` 祖先或子 `Window` 自身上才正常）。换成 `gui/theme/custom` 之后这个问题自然消失，
> `apply_to()` 也就删掉了。

配色按浏览器深色模式取值 —— **不是纯黑**：纯黑配白字对比过强，看久了累。

| 类型 | 值 | 用途 |
|:---|:---|:---|
| 窗口底 | `#202124` | 输入框、**画面四周的留白**（视口清屏色，不是纯黑） |
| 面板/菜单栏 | `#292a2d` | 面板、菜单栏、状态栏（**菜单不是黑底**） |
| 悬停 / 按下 | `#35363a` / `#3c4043` | |
| 强调色 | `#8ab4f8` | 悬停文字、焦点边框 |
| 主文字 / 次要文字 | `#e8eaed` / `#9aa0a6` | |
| 分隔线、边框 | `#3c4043` | |
| 字号 | 正文 `20`、次要 `18` | Godot 默认是 16，这里整体放大一档半 |

覆盖的控件（`.tres` 里的 16 个类型）：`Label` / `RichTextLabel` / `Panel` / `PanelContainer` /
`Button` / `MenuBar` / `PopupMenu` / `LineEdit` / `ItemList` / `Tree` / `FileDialog` /
`HSeparator` / `VSeparator`。

**两个类型变体**（tscn 里写 `theme_type_variation = "DimLabel"` 就能用）：

- `DimLabel`：次要说明文字，暗一档、小一档（18）。**表头、行标题、寄存器名统一用它** ——
  早期是每个节点各写一遍 `theme_override_font_sizes/font_size` + `theme_override_colors/font_color`，
  字号写死在场景里，改主题要挨个文件改。
- `MonoLabel`：需要对齐的值（寄存器、校验和、栈顶），挂在 `SystemFont` 生成的等宽字体上
  （`Consolas` → `DejaVu Sans Mono` → `Courier New` → `monospace`，跨平台逐个回退）。
  调试窗口里那 16 个 `*Value` 标签都是它。

**视口清屏色**（画面四周的留白）本来不属于 `Theme` 能管的范围，所以 .tres 里放了一个自定义条目
`NesTheme/colors/window_background`，`main.gd` 通过 `ThemeDB.get_project_theme().get_color(...)` 取出来
交给 `RenderingServer.set_default_clear_color()` —— 颜色值仍然只在主题文件里。

**独立调试窗口要自己跟缩放**（这是"调试窗口里的字看起来很小"的根因）：

主窗口用 `canvas_items` 拉伸（设计分辨率 1024×768）：实际窗口比设计尺寸大多少，界面就放大多少倍。
**独立 `Window` 不吃这套** —— 它的 `content_scale_factor` 默认是 1，于是主窗口放到 1.5 倍时，
调试窗口里的字看上去就比主界面小一圈。

`DebugWindow._follow_main_window_scale()` 在主窗口尺寸变化时把同一个缩放比抄过来，
并按比例放大窗口尺寸（`size = 设计尺寸 × 缩放比`，不然逻辑尺寸不变、内容会被裁掉）。
冒烟测试里带窗口那一遍会把主窗口临时放大到 2 倍，验证调试窗口的缩放和尺寸都跟着变、缩回去也跟得回来。

### 12.6 国际化（`lang/language.csv` + `autoload/ui_settings.gd`）✅

**表结构**：第一列是 key，也是**中文原文**，后面每列一种语言；目前只有 `en` 一列。

```csv
keys,en
文件,File
打开 ROM…,Open ROM…
```

**为什么中文不单独列一列**：中文就是 key 本身，`tr("文件")` 在中文下本来就该原样返回。
但**必须同时把 `internationalization/locale/fallback` 设成空字符串**（`project.godot`）：

> Godot 的 `TranslationServer.translate()` 在当前 locale 查不到时会**依次去 fallback locale 查**，
> 而 fallback 默认就是 `"en"`。于是中文环境下 `tr("文件")` 会命中 en 表返回 `"File"` ——
> 整个中文界面会变成英文。fallback 置空后查不到就直接返回原文（= 中文）。
> 这个坑实测过：改之前 `tr("文件") == "File"`，改之后 `== "文件"`（冒烟测试里有断言守着）。

**几个约定**

- key 里**不能有逗号**：Godot 的 CSV 翻译导入器是朴素按逗号切列的，带逗号的值会被切成两列。
  英文里需要逗号的地方一律用 `-` 或 `;`（例：`No CHR ROM - uses %d KB CHR RAM`）。
- **值的首尾空格会被保留**，拼字符串的 key 靠它：`已装载：` → `Loaded: `（注意尾部那个空格）。
- `%d` / `%s` 占位符在原文和译文里都要原样保留，翻译走 `tr("...%d...") % x`。
- 一个 key 可以被多处复用（`文件` 既是菜单名也是 ROM 信息里的行标题）。

**切换语言的落点**（`UiSettings.set_language(code)` → `TranslationServer.set_locale()` → `locale_changed` 信号）：

| 谁 | 怎么重译 |
|:---|:---|
| 菜单栏 `main.gd` | 整个**重建**菜单（条目多，逐个 `set_item_text` 更容易漏） |
| `main.gd` 状态栏 / 对话框 | 重新拼一遍文字 |
| 三个调试窗口 | 覆写 `_retranslate()`：静态表头按 `_static_texts()` 重设，动态内容用缓存的快照原地 `_refresh()` |
| tscn 里的静态文字 | 由基类 `DebugWindow._retranslate()` 按子类登记的「节点唯一名 → 中文原文」重设 |
| 窗口标题（`Window` 不是 `Control`） | 各窗口自己 `tr()` 后设 `title` |

**两个被实测教育过的点**

1. **重建菜单必须 `remove_child()` + `free()`，不能用 `queue_free()`。**
   `MenuBar` 内部有一份 `menu_cache`，在 `add_child` / `remove_child` 时**同步**维护，而
   `set_menu_title(i, title)` 是往 `menu_cache[i].name` 上写、再交给 `shape()` 排版。
   `queue_free()` 是延迟释放：那一帧里旧菜单还挂在 `menu_cache` 上（6 个变 12 个），
   新菜单被挤到后半段，`set_menu_title(0, ...)` 设到了**旧节点**上，标题会显示成
   `@PopupMenu@351` 这种自动生成的名字。这个 bug 在冒烟测试里以 "菜单 0 = File" 的形式暴露出来。
2. **Godot 的 `Control` 在排版时会自动翻译自己的文字（`atr()`）**，也就是说
   `Label.text = "文件"` 在英文环境下**画出来**就是 `File`（属性值仍是 `文件`）。
   本项目仍然显式 `tr()` 后再赋值，好处是 `.text` 读出来永远是当前语言 —— 无头测试能直接断言
   文字对不对，而不必去比较排版宽度。注意引擎的自动翻译是**额外**叠在上面的，
   两者都要求 fallback 为空，否则中文环境下都会被悄悄换成英文。

**C# 快照里的中文值也要 tr()**：PPU 窗口的"名称表镜像：xxx"里那个 xxx 是 C# 侧给的中文原文
（`水平`/`垂直`/`单屏(低)`…），窗口脚本里要再 `tr()` 一次才跟着语言走 —— 这几条正好都在语言表里。

### 12.7 测试 ✅

| 手段 | 位置 | 用途 |
|:---|:---|:---|
| 冒烟测试 | `tests/smoke_test.tscn` | **144 项断言**，覆盖 autoload、主场景、菜单、窗口数据链路、ROM 解析、错误路径、主题、中英切换、mapper 支持列表、**手柄输入** |
| **CPU 差分测试** | `tests/cpu_diff.ps1` | 拿 fogleman/nes 当参照逐行比对 CPU 状态，**800 行一致** |
| **PPU 差分测试** | `tests/ppu_diff.ps1` | 比对渲染画面，**两个 ROM 各 61440 像素逐字节一致** + 手算核对 + NMI |
| **APU 测试** | `tests/apu_test.ps1` | 奏已知频率的音再量回来：静音精确为 0、脉冲 440.7 Hz、三角 219.9 Hz |
| 酷刑 ROM | `tests/make_torture_rom.js` | 官方指令 + 寻址模式全跑一遍，含跨页、分支、BRK |
| PPU 测试 ROM | `tests/make_ppu_test_rom.js` | 真正会配 PPU 并画图的 6502 程序：`ppu-test.nes`（滚动 0,0）、`ppu-scroll.nes`（滚动 3,5）、`ppu-sprite0.nes`（精灵 0 命中与优先级的回归） |
| APU 测试 ROM | `tests/make_apu_test_rom.js` | 4 个 ROM：静音 / 440 Hz 脉冲 / 220 Hz 三角波 / 噪声 |
| **mapper 测试** | `tests/mapper_test.ps1` | **自检 66 条 + 与参照实现 5 × 2000 行 trace 一致 + MMC3 IRQ 次数符合理论**（见下） |
| mapper 测试 ROM | `tests/make_mapper_test_rom.js` | 6 个 ROM：mapper 1/2/3/4/7 各一块 + MMC3 扫描线 IRQ 一块 |
| 测试 ROM 生成 | `tests/make_test_rom.ps1` | 生成 7 个合成 `.nes`（4 个正常 + 3 个坏文件） |
| ROM 信息 | `--rom-info` | 无头检查单个 ROM 的解析结果，失败退出码 1 |
| CPU trace | `--trace-cpu N --trace-out <路径>` | 输出每步 CPU 状态（**只单步 CPU**），CPU 指令级差分的基础 |
| 整机 trace | `--trace-console N --trace-out <路径>` | 同上，但 PPU/APU 一起跑、NMI/IRQ 照常递送 —— 查「游戏跑起来之后在哪一步分岔」必须用它 |
| 帧比对 | `--dump-frame-indices <路径>` | 导出 PPU 的调色板索引帧（61440 字节），适合逐字节比 |
| 内存窥视 | `--dump-ram <地址> <长度>` | 打印一段 CPU RAM，用来核对 NMI 计数之类的副作用 |
| 音频导出 | `--dump-audio <路径>` | 导出 16 位单声道 PCM，用来核对频率等物理量 |
| 画面校验 | `--dump-frame <png>` | 导出 PNG，人工看或配合像素断言 |

冒烟测试跑法（退出码 0 = 全过）：

```powershell
& "E:\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64_console.exe" `
  --headless --path "E:\godot-nes\godot-nes" "res://tests/smoke_test.tscn"
```

**关键决策**

- 断言要覆盖**错误路径**（不支持的 mapper / 截断 / 魔数不对），而不只是 happy path。
- 无头下 `Window` 没有真实屏幕尺寸，`popup_centered()` 会报位置无效，
  所以"窗口弹出"和"数据正确"分开断言，无头也能测数据链路。
- 国际化要有**真刀真枪切一次语言**的断言（菜单标题 / 窗口标题 / tscn 静态表头 / 带占位符的翻译 /
  结尾空格 / 切回中文），只验证"翻译表能加载"是发现不了 fallback 那个坑的。

### 12.8 mapper 测试的做法（`tests/make_mapper_test_rom.js` + `mapper_test.ps1`）

mapper 的坑是"**换 bank 换错了但画面还要过一会儿才看得出来**"，所以测试 ROM 不是画图，
而是**让 6502 程序自己去戳**：

1. 每块 PRG bank 的开头放一个特征字节（16KB bank k → `$10+k`，8KB → `$20+k`，32KB → `$30+k`）；
   每块 CHR 的 1KB 里，**tile 1** 的 16 个字节全部写成"这块 CHR 在整块 CHR 里的序号"当身份标记
   （放在 tile 1 是为了不干扰"整屏铺 tile 0"的画面对比）。
2. 程序真的去写换 bank 寄存器，然后从各个窗口读回来：
   - PRG 直接读 `$8000/$A000/$C000/$E000`；
   - **CHR 也能从 CPU 读**：PPU 的 `$0000-$1FFF` 就是图案表，走 `$2006/$2007` 设地址 + 哑读 + 真读
     就能把当前映射的 CHR 字节读回来 —— 于是"CHR 换 bank"也能用手算值验，不必画图；
   - 镜像：往 `$2000` 写一个值，再从 `$2400/$2800/$2C00` 读回来，就能看出四块名称表怎么映射的。
3. 每个期望值都**照着 nesdev 手算**，不等就 `INC $03F0`（失败计数），同时把实际值记在 `$0310+X`；
   跑完写 `$03F1` = 条数、`$03F2` = `$A5`（完成标志）。测试脚本用 `--dump-ram 03F0` 读出来核对。

**为什么要"手算期望 + 参照实现差分"两道关**：

- 只做差分：两边一起错就发现不了（比如都以为 `$A000` 复位后是第 1 块）；
- 只做手算：人算错了会以为自己错了。两道关一起过才算数。

**踩到的坑**（都是写测试 ROM 时踩的，记下来免得再犯）：

| 坑 | 现象 |
|:---|:---|
| 汇编器的标签按"文件内偏移"算 | `JSR` 的 16 位操作数写成了 `$005E` 这种，PC 飞到 RAM 里。标签必须按**代码将来所在的 CPU 地址**算 |
| 比较例程放在主流程后面 | 主流程最后一个 `JSR` 返回时一头掉进例程体，用空栈再 `RTS` 一次。例程要放在"永远跳自己"的收尾之后 |
| AxROM 只在最后一块 bank 写向量表 | AxROM 上电用哪块 bank 不确定，CPU 从 `$EAEA` 的 NOP 海开始跑。**向量表和代码都要在每块 32KB bank 里各来一份** |
| 测试里写 `LDA #$10 / STA $8000` 以为"bank 0 + 单屏低" | `$10` 的 bit4 就是镜像位，实际是"bank 0 + 单屏**高**"。bank 位是 0-3、镜像位是 bit4 |
| MMC3 复位后读 `$A000` | 真机上 R7 的上电值不确定；取 FCEUX 惯例 R7 = 1（`$A000` = 第 1 块）后才和参照实现一致 |

---

## 13. 变更记录

### 2026-09-10 · 阶段 0：工程骨架 + ROM 读取

**工程与界面**

- 建立 Godot 4.7.2 mono 工程，目录结构对齐 `machine-TD`（`scene/` `script/` `autoload/` `ui/` + `core/`）。
- 离线构建：`NuGet.config` 指向 Godot 自带的 `GodotSharp\Tools\nupkgs`；
  `TargetFramework` 定为 `net10.0`（本机没有 .NET 8 运行时，靠 `rollForward=LatestMajor`）。
- 主界面改为 GDScript（菜单 + 画面 + 状态栏），调试信息搬到**独立 `Window`**。
  `display/window/subwindows/embed_subwindows` 必须显式设为 `false`（默认 `true` 会嵌进主窗口）。
- 三个调试窗口：CPU / PPU / ROM 信息，关闭即隐藏、可见才订阅。

**核心**

- `.nes` 读取完整实现（iNES 1.0 + NES 2.0），字段与错误处理见 §4。
- `Bus` 内存映射、`Controller` 手柄、`Ppu` 寄存器与地址通路、`NesPalette`。
- `cpu` / `APU` 为桩；`Diagnostics` 提供阶段 0 占位画面。

**验证**

- `dotnet build` 0 警告 0 错误；冒烟测试 63/63 通过（无头 + 带窗口各一遍）。
- 帧缓冲逐像素校验：pattern table / 分隔线 / 调色板色块 / 棋盘格 / 灰阶全部正确。

**杂项**

- 统一了 C# 缩进。注意：**Godot 编辑器会把"内容改动过"的 `.cs` 重排成制表符缩进**
  （只影响它重新导入的那些文件；命令行 `--import` 和直接跑项目都复现不出来，
  所以当时误判成"Godot 不会重排"）。既然工具用制表符，约定就统一成制表符，见 §2.9。

### 2026-09-11 · 阶段 1：CPU 6502

**核心**

- `core/CPU/` 拆成四个文件：`Opcode.cs`（枚举）、`OpcodeTable.cs`（生成的 256 项矩阵）、
  `Cpu.cs`（主循环 + 寻址 + 栈 + 中断 + trace）、`Cpu.Instructions.cs`（56 条指令）。
- 官方 56 条指令全部实现，周期数对齐 nesdev 官方表：基础周期在表里，
  跨页附加周期只给"读"类指令，分支的 +1/+2 在 `Branch()` 里返回。
- JMP 间接的跨页 bug、BRK 的 2 字节语义、`P` 的 B/U 位语义都按真机处理。
- 非官方 NOP 变体按 NOP 处理；其余非法指令**停下并报告**（不再假装在跑）。
- `Cpu.LastOpcodeWasUnimplemented` → `HitIllegalOpcode`，
  `NesConsole.UnimplementedOpcode` → `IllegalOpcode`，CPU 窗口文案同步更新。

**验证工具（新增）**

- `--trace-cpu <条数>` / `--trace-out <路径>`：单步执行并输出每步的 CPU 状态。
- `tests/make_torture_rom.js`：从**参照实现**读指令清单和长度，生成"酷刑 ROM"。
- `tests/cpu_diff.ps1`：拿 fogleman/nes（Go）当参照逐行比对。
  `.ref/` 放参照实现（第三方源码，已 gitignore）。

**验证**

- `dotnet build` 0 警告 0 错误；冒烟测试 63/63 通过。
- **CPU 差分测试 800 行完全一致**（寄存器、标志位、周期数全部对得上）。
- 顺手修了测试 ROM 生成器的一个 off-by-one：fixup 写在操作码位置上，
  把 `JSR`/`JMP`/分支的操作码覆盖成了地址字节，导致控制流段落整个是坏的。

**性能**

- 600 帧（约 900 万条指令）连启动带装载 5.3 秒，远没到瓶颈。

### 2026-09-11 · 阶段 3/4：PPU 渲染

**核心**

- `core/PPU/Ppu.cs` 加扫描线时序：262 条扫描线、VBlank（241 行置位、pre-render 清）、
  `TakeNmiRequest()`；渲染结果先写 6 位索引缓冲，进 VBlank 时统一转 RGBA。
- 新增 `core/PPU/Ppu.Rendering.cs`：背景（名称表 + 属性表 + 图案表 + 调色板 + 滚动）
  和精灵（8x8 / 8x16、水平/垂直翻转、背景优先级、精灵 0 命中、精灵溢出）。
- `NesConsole` 改成 CPU/PPU **同步推进**（每执行一条指令，PPU 跑 3 倍周期），
  NMI 在指令之前递送。
- 删掉 `core/Diagnostics/DiagnosticRenderer.cs` 和 `NesConsole.RenderPlaceholderFrame()` ——
  PPU 自己会画了，阶段 0 的占位画面功成身退。

**验证工具（新增）**

- `--dump-frame-indices <路径>`：导出 PPU 的调色板索引帧。
- `--dump-ram <地址> <长度>`：打印一段 CPU RAM（用来核对 NMI 计数）。
- `tests/make_ppu_test_rom.js`：写了一段**真正的 6502 程序**去配置 PPU（调色板、名称表、
  属性表、OAM DMA、滚动、开 NMI），生成 `ppu-test.nes`（滚动 0,0）和 `ppu-scroll.nes`（滚动 3,5）。
- `tests/ppu_diff.ps1`：和 fogleman/nes 的画面逐像素比对 + 手算核对关键像素 + NMI 计数。

**差分测试抓到的两个真 bug**

1. **属性表的列方向移位算错**：写成 `(coarseCol & 2) << 1`，多左移了一位 →
   屏幕右半部分的调色板全错（30720/61440 个像素不同）。正确是 `(coarseRow & 2) << 1) + (coarseCol & 2)`。
2. **写 `$2000` 没有把名称表位拷进 `t`**：真机会拷 bit0-1 到 `t` 的 bit10-11，
   少了这一步"滚动到另一块名称表"就失效。这个是读代码时发现的，随后用滚动版 ROM 验证。

**验证**

- **两个 PPU 测试 ROM 的画面各 61440 字节逐字节一致**；手算核对的 15 个关键像素全中；
  NMI 每帧计一次（10 帧计到 7~8 次）。
- 冒烟测试 63/63、CPU 差分测试 800 行一致，都无回归。
- 一个测试方法上的坑：滚动版最初用**水平镜像**，结果写 `$2400` 和写 `$2000` 落到同一块
  物理名称表上，"名称表切换"根本测不到。改成垂直镜像后才真正覆盖到（x=253 起切到第二块表）。

**性能**

- 600 帧连启动带装载 7.15 秒（加渲染前是 5.3 秒），仍然远没到瓶颈。

---

## 14. 已知问题

| 问题 | 影响 | 处置 |
|:---|:---|:---|
| `--headless` 退出时报 `ObjectDB instance was leaked`（`AudioStreamGeneratorPlayback`） | 无 | Godot 内部的 audio playback 引用计数，带窗口运行不出现；已在 README 说明 |
| `--headless` 下 `popup_centered()` 报 `Window 0 spawned at invalid position` | 无 | 无头没有屏幕尺寸；窗口与数据分离断言即可 |
| 受限沙箱里跑会报 `user://logs` 创建失败 | 无 | Godot 写不了 `%APPDATA%\Godot`，与项目无关 |
| 没有生成 `.sln` | 无 | Godot 构建的是 `.csproj`；编辑器若提示创建解决方案，接受即可 |
| `mapper 4` 等样本 ROM 无法运行 | 预期 | 阶段 7 实现 |

---

## 15. 下一步

CPU、PPU、APU 都已完成并各自通过验证 —— **画面和声音都有了**。接下来按计划：

**阶段 5：输入与可玩性（工作量最小，性价比最高，建议先做）**

- [x] ~~玩家 2 的输入映射~~ → **已完成**（`nes2_*` 动作 + `Controller2`，见 §9 / §11.4）。
- [x] ~~用真实 ROM 验证：装载《超级马里奥兄弟》，确认标题画面 → 1-1 能操作~~ → **已完成**
      （2026-09-12，修掉了精灵 0 命中导致的卡死；差分方法见 §13）。

**阶段 7：Mapper 扩展（决定能玩多少游戏）**

- [x] ~~MMC1 / UxROM / CNROM / MMC3（含扫描线 IRQ）~~ → **已完成**，另外还做了 mapper 7 (AxROM)。
      0/1/2/3/4/7 覆盖了 NES 上绝大多数商业卡带；验证方式见 §4.8 与 §12.8。
- [ ] 继续扩展：MMC2/MMC4（PxROM，图案表按 A12 切）、Konami VRC 系列、Namco 163、MMC5。
- [ ] 用真实 ROM 验证：装一个 MMC1 的《塞尔达传说》和一个 MMC3 的《超级马里奥兄弟 3》，
      确认标题画面 → 关卡内可操作（MMC3 那份还能顺带看分屏状态栏）。

**阶段 8 及零散欠账**

- [ ] `$4014` OAM DMA 的 CPU 停顿（513/514 周期）。
- [ ] 非法指令（很多日版游戏会用到）。
- [ ] PAL 制式的帧周期数。
- [ ] PPU 的精灵 0 命中 dot 级时机、色彩强调位、`$2002` 读后的 NMI 抑制。
- [ ] APU 的 DMC 周期偷取、三角波线性计数器细节、输出低通滤波。
- [ ] 电池存档（`.sav`）。
- [ ] APU 调试窗口（各通道电平和寄存器，现在只有 CPU / PPU / ROM 三个窗口）。
- [ ] PPU 的精灵 0 命中 dot 级时机、色彩强调位、`$2002` 读后的 NMI 抑制。
- [ ] 电池存档（`.sav`）。

**验证方式沿用现在的思路**：官方测试 ROM（`nestest`、`ppu_vbl_nmi`、`sprite_hit_tests`）
到手后补上；在此之前，差分测试 + 手算核对 + 自写测试 ROM 这套组合已经能抓住真 bug
（PPU 阶段就抓到了两个）。

### 2026-09-11 · 阶段 6：APU 音频 + 关闭 ROM

**核心**

- `core/APU/Apu.cs`：寄存器、帧计数器（4 步 / 5 步、含 IRQ）、按 CPU 周期推进、
  整数累加器采样、NES 非线性混音、样本缓冲。
- `core/APU/ApuChannels.cs`：`LengthTable` + `Envelope`（脉冲和噪声共用）+
  `PulseChannel`（占空比/长度/包络/扫频）、`TriangleChannel`（线性计数器）、
  `NoiseChannel`（15 位 LFSR）、`DmcChannel`（差分调制，读总线取采样）。
- `NesConsole` 现在同步驱动 CPU/PPU/APU（1 : 3 : 1），帧末把样本交给 `AudioSink`；
  中断递送也补上了 APU 的帧 IRQ。
- `NesConsole.EjectCartridge()` + `EmulatorService.CloseRom()`：卸载卡带、清空 ROM 信息；
  没有卡带时不再推进帧。

**界面**

- 菜单新增「文件 → 关闭 ROM」，没装 ROM 时置灰。
- `EmulatorService` 新增 `RomClosed` 信号，`DebugHub` 订阅后刷新 ROM 信息窗口。

**验证工具（新增）**

- `--dump-audio <路径>`：导出 16 位单声道 PCM（44100 Hz）。
- `tests/make_apu_test_rom.js`：4 个 ROM（静音 / 440 Hz 脉冲 / 220 Hz 三角波 / 噪声）。
- `tests/apu_test.ps1`：核对物理量而不是逐字节比对。

**验证**

- 冒烟测试 **71/71**（新增 8 项关闭 ROM 的断言）。
- **APU 测试通过**：静音 ROM 输出精确为 0；脉冲量到 440.7 Hz（理论 440.4）、
  峰值 4894（理论 4895）；三角波量到 219.9 Hz（理论 220.2）、峰值 8074（理论 8070）；
  噪声峰值 5715、过零 6791 次。
- CPU / PPU 差分测试无回归。

**踩到的坑**

- 频率统计一开始数"符号变化"，结果一个都没数到 —— 混音输出是**单极性**的
  （0 到正的峰值），从不变负。改成用中值当阈值才对。

**性能**

- 600 帧连启动带装载 8.95 秒（加 APU 前是 7.15 秒），仍然远没到瓶颈。

### 2026-09-11 · 界面打磨：深色主题 + 中英文国际化

**主题（`autoload/ui_theme.gd`，新增 autoload）**

- 整套 `Theme` 用代码构建，主界面挂在 `$Ui/Root`、三个调试窗口和对话框各挂自己
  （**根 Window 的 theme 不会往下传**，见 §12.5）。
- 配色按浏览器深色模式：窗口底 `#202124`、面板与菜单栏 `#292a2d`、文字 `#e8eaed`、
  次要文字 `#9aa0a6`、强调色 `#8ab4f8`、边框 `#3c4043` —— **菜单栏不再是黑底**，和面板同色。
- 字号整体放大一档（默认 16 → 17），调试窗口里的表头/说明 15。
- 新增两个类型变体：`DimLabel`（次要说明文字）、`MonoLabel`（等宽，给寄存器/校验和用）。
- 场景里 22 处 `theme_override_font_sizes` + `theme_override_colors` 全部换成 `theme_type_variation`，
  字号不再写死在场景文件里；16 个寄存器值标签补上了 `MonoLabel`。

**国际化（`lang/language.csv` + `UiTheme.set_language`）**

- CSV 翻译表：第一列是 key（= 中文原文），目前有 `en` 一列，97 条。
- `project.godot` 新增 `internationalization/locale/fallback=""`（原因见 §12.6，**不设这个中文界面会变英文**）。
- 菜单新增「语言」菜单：中文 / English 单选项，勾选状态跟着当前语言走；
  启动时按 `OS.get_locale()` 决定默认语言（英文系统→英文，其余→中文）。
- 调试窗口基类新增 `_retranslate()` 钩子 + `_static_texts()` 登记表 + 最近快照缓存：
  切语言时静态文字重设、动态内容用缓存快照原地重画，不用等下一次 12 Hz 刷新。

**冒烟测试加强**

- 新增一段真的切语言（中 → 英 → 中）的断言：菜单标题、窗口标题、tscn 静态表头、
  带 `%s` 占位符的翻译、结尾空格、同一个 key 的两种用法、切回中文后都恢复。
- 新增一段主题断言：菜单栏底色是 `#292a2d` 而不是黑的、字号 17 / 15、
  `MonoLabel` 真的取到等宽字体、`DimLabel` 的文字更暗。
- 顺带修掉两个真 bug：菜单重建用 `queue_free()` 会撞上 `MenuBar` 内部的菜单表（见 §12.6）；
  主题挂在根 Window 上等于没挂（见 §12.5）。
- 冒烟测试 **107/107**（原 71 项 → 关闭 ROM 8 项 → 国际化 21 项 → 主题 13 项，中间有调整）。

**踩到的坑**

- **fallback 会把中文环境变成英文**：Godot 在请求的 locale 里查不到就退到 fallback（默认 `en`），
  于是 `tr("文件")` 在中文下返回 `"File"`。要么加一列 `zh`（中文重复一遍），要么把 fallback 置空 ——
  选了后者，CSV 保持两列。
- **CSV 里不能有逗号**：导入器是朴素按逗号切列，带逗号的译文会被切成两列。
- **`MenuBar` 的标题不在 MenuBar 上**：标题存在内部 `menu_cache[i].name`，
  由 `add_child` 时的 `PopupMenu.title`（空则用节点名）算出来，`get_menu_title()` 读的是它。
- **根 Window 的 `theme` 不往下传**：见 §12.5，必须挂到控件树的根上。
- **不要用 PowerShell 的 `Set-Content` 改 `.tscn`**：PS 5.1 的 `-Encoding UTF8` 会写 BOM，
  而 `Get-Content` 默认按 ANSI 读，来回一趟中文全变乱码（`表 $0000` → `琛?$0000`）。
  改文本文件一律用编辑器 / `[System.IO.File]::WriteAllText(..., UTF8Encoding($false))`。

**验证**

- 冒烟测试 107/107；CPU 差分 800 行一致；PPU 两个 ROM 各 61440 像素逐字节一致；APU 四项物理量不变。
- 主题和排版无法在无头下"看"，能验证的是：脚本无解析错误、所有控件的文字/变体都取得到、
  切语言后属性值正确。

### 2026-09-11 · 映射器：MMC1 / UxROM / CNROM / MMC3 / AxROM

**核心（`core/Cartridge/`）**

- `Mapper.cs` 补齐了子类要用的东西：`DynamicMirroring`（映射器自己改镜像）、
  `PrgBank` / `ChrBank`（bank 号 → 字节偏移，支持负数与取模绕回）、`ReadChr` / `WriteChr`（ROM/RAM 统一）。
- 五块新映射器：`Mmc1Mapper`、`UxromMapper`、`CnromMapper`、`Mmc3Mapper`、`AxromMapper`；
  `MapperFactory` 注册成 `0/1/2/3/4/7`。
- `NromMapper` 改用公共工具重写，行为不变（差分测试守着）。

**接线**

- `Ppu.EffectiveMirroring = Mapper?.DynamicMirroring ?? Mirroring`，渲染取名称表改用它；
- `Ppu.BeginScanline` 在**渲染开着**时每渲染完一条可见扫描线调一次 `Mapper.OnScanline()`
  （真机没有 A12 上升沿时计数器不动）；
- `NesConsole.StepInstruction` 把 `Mapper.IrqPending` 和 APU 的帧 IRQ 并到同一根 IRQ 线上；
- `NesConsole.Reset` 顺带 `Mapper.Reset()`；
- `EmulatorService` 新增 `GetImplementedMappers()`，PPU 快照新增 `mapper` / `mapper_irq`，
  `mirroring` 改成**当前生效**的值。

**上电值按主流惯例取**（真机上不确定，只影响游戏初始化之前）：MMC1 控制寄存器 `$1F`、MMC3 的 R0-R7 = `0,2,4,5,6,7,0,1`，都跟 FCEUX 一致。

**测试（新增 `tests/make_mapper_test_rom.js` + `tests/mapper_test.ps1`）**

- 6 块测试 ROM，程序真的去写换 bank 寄存器，再把读回来的字节和**手算期望**比：
  **66 条自检全过**（PRG / CHR / 镜像都覆盖，CHR 是从 CPU 走 `$2006/$2007` 读回来验的）；
- 同样这几块 ROM 和 fogleman/nes 逐行比 CPU trace：**5 × 2000 行完全一致**；
- MMC3 扫描线 IRQ：跑 12 帧触发 **28 次**（理论 12 × 240 / 101 ≈ 28.5）；
- 冒烟测试 **127/127**（新增 mapper 支持列表、已实现 mapper 号、PPU 快照字段的断言）。

**踩到的坑**（都记在 §12.8 的表里）

- 汇编器的标签按"文件内偏移"算 → `JSR` 操作数错，PC 飞进 RAM。标签必须按**代码将来所在的 CPU 地址**算；
- 比较例程放在主流程后面 → 主流程 `JSR` 返回时掉进例程体，用空栈再 `RTS` 一次。例程要放在收尾的
  "永远跳自己"之后；
- AxROM 只在最后一块 bank 写向量表 → 上电用哪块 bank 不确定，CPU 从 `$EAEA` 的 NOP 海开始跑。
  **向量表和代码都要在每块 32KB bank 里各来一份**（真机 AOROM 游戏就是这么排的）；
- 测试里 `LDA #$10 / STA $8000` 想表达"bank 0 + 单屏低"，但 `$10` 的 bit4 就是镜像位 ——
  实际是"bank 0 + 单屏高"。bank 位 0-3、镜像位 bit4；
- MMC3 复位后读 `$A000`：R7 的上电值不确定，取 FCEUX 惯例（R7 = 1）后才和参照实现一致；
- MMC1 只在"第一次寄存器写"之后才进入确定状态：参照实现（fogleman/nes）的构造器把初始 offset
  设成"最后一块固定在 `$C000`"但模式变量仍是 0，第一次 `updateOffsets()` 就会掉进 32KB 模式的分支。
  真游戏开头都写 `$80`（复位移位寄存器 + 强制模式 3），所以测试 ROM 也照做 —— 不去依赖未定义行为。

**验证**

- 冒烟 127/127；CPU 差分 800 行一致；PPU 两个 ROM 逐像素一致；APU 四项物理量不变；mapper 测试全过。

### 2026-09-11 · 主题改成资源文件 + 放大调试窗口的字

用户反馈两条：样式应该放在**主题/样式文件**里，不要用代码设置；独立调试窗口里的**字很小**。

**改成资源文件**

- 新增 `theme/dark_theme.tres`（Godot `Theme` 资源，16 个类型），`project.godot` 里
  `gui/theme/custom` 指向它 —— 项目主题是主题查找链的全局兜底，主窗口、每个独立 `Window`、
  运行中 new 出来的对话框全都吃得到。
- 删掉 `autoload/ui_theme.gd` 里的 `_build_theme()` / `apply_to()` / `mono_font` / 一堆颜色常量；
  autoload 改名 `UiSettings`（`autoload/ui_settings.gd`），只留语言切换。
- 视口清屏色（画面留白）塞进主题的自定义条目 `NesTheme/colors/window_background`，
  `main.gd` 从 `ThemeDB.get_project_theme()` 取 —— 颜色值仍然只在主题文件里。
- `.tres` 是用一次性脚本（`_build_theme()` 的移植版）生成后删掉脚本的：手写 StyleBoxFlat 的几十个
  属性容易写错一个字段，让 Godot 存出来的一定合法。之后改样式用 Godot 的主题编辑器。

**字号 + 缩放**

- 字号整体再放大：正文 17 → **20**，次要说明 15 → **18**（Godot 默认 16）。
- **真正的原因是缩放不是字号**：主窗口用 `canvas_items` 拉伸（设计分辨率 1024×768），
  窗口比设计尺寸大多少界面就放大多少倍；而**独立 `Window` 不跟着拉伸**
  （`content_scale_factor` 默认 1），所以主窗口一放大，调试窗口里的字看上去就小一圈。
  `DebugWindow._follow_main_window_scale()` 把主窗口的缩放比抄过来，窗口尺寸也按比例放大。

**验证**

- 冒烟测试 **130/130**（无头）/ **131/131**（带窗口）。
  带窗口那一遍会真的把主窗口放大到 2 倍，验证调试窗口的 `content_scale_factor` 和尺寸都跟着变、
  缩回去也跟得回来 —— 这条断言就是冲着这次的报障加的。
- 顺带把主题断言改成查"**项目主题** + 控件查出来的值"：不再看"有没有把主题挂到某个节点上"，
  因此"根 Window 的 theme 不往下传"那个坑在新写法下自然不存在了。
- CPU / PPU / APU / mapper 四套测试无回归。

### 2026-09-12 · 修《超级马里奥兄弟》跑不起来（精灵不显示、按开始没反应）

**现象**：装 SMB 后标题画面能画出来，但**山丘上的马里奥不出现**，按开始也没反应 —— 游戏其实卡死了。

**排查方法**（这套流程值得留着，比"盯着代码看"快得多）：

1. **CPU trace 差分**（`--trace-cpu` vs `nesoracle`）：前 8 行就分岔，抓到 `$2002` 少了开放总线的低 5 位。
2. **整机 trace 差分**：给 `EmulatorCore` 加了 `--trace-console`（整机一起跑，NMI/IRQ 照常递送），
   给 oracle 加了 `traceframe`；再用一个能"容忍错位"的比较脚本（`.ref/tracediff.js`）找真正跑不同代码的地方。
   ——只单步 CPU 的话 PPU 不动，游戏永远卡在等 VBlank 的循环里，什么都测不出来。
3. **RAM 差分**：给 oracle 加了 `ram` 模式（跑 N 帧后 dump 一段 CPU RAM），
   两边逐字节比。这一步把范围缩到"第 33~34 帧之间游戏状态开始跑偏"。
4. **画面差分**：参照实现的 `frame` 模式输出的是**调色板索引**（它是从 RGBA 反查的），
   而 NES 调色板里有重复颜色（`$20` 和 `$30` 都是纯白），直接比索引会看到一堆"假不同"；
   改成**按 RGB 比**之后，差异从 5356 像素缩到 143 像素 —— 正好就是马里奥那一块。
5. **第三方裁判**：拿 jsnes（纯 JS，能 headless 跑）跑同一个 ROM 出图，确认"马里奥本来就该站那儿"。

**改掉的四个问题**

| 问题 | 表现 / 影响 |
|:---|:---|
| **精灵 0 命中和优先级的顺序错了**（根因） | 先判 `behindBackground` 就 `continue`，导致带"在背景后面"属性的精灵 0 永远不置位。SMB 的标题画面在 `$8150` 死等这个标志（`LDA $2002 / AND #$40 / BEQ`），于是**整个游戏卡死在 NMI 处理程序里** —— 精灵不再更新、手柄也不再读。修法：命中判定放到优先级判定**之前**，并且按真机规则用 `x < 255` 判最右一列（左 8 像素由 `_backgroundOpaque` 体现） |
| `$2002` 缺开放总线低 5 位 | 真机返回"最后一次写进 PPU 寄存器的值"的低 5 位。少了它 SMB 开机等待 VBlank 循环里的 A / Z 标志就和真机不一样（`tr("文件")` 那种级别的差异，但 trace 差分一眼就看出来了） |
| `$4014` OAM DMA 没有 CPU 停顿 | 真机 513/514 周期不取指。少了它整机时序整体偏 —— 参照实现的 trace 里那 514 行"原地重复"就是这个停顿 |
| 帧边界没对齐 PPU | `StepFrame()` 原来按"固定 29780 个 CPU 周期"切帧，和 PPU 的帧（89342 dot）差 2 dot，帧号会漂，`--dump-frame-after N` 还可能抓到撕裂画面（SMB 前几帧整屏都不一样就是这个）。改成和参照实现同一套语义：**跑到 PPU 帧计数器 +1 为止** |

**新增的回归测试**

- `tests/make_ppu_test_rom.js` 多生成一块 **`ppu-sprite0.nes`**：精灵 0 带"在背景后面"属性、
  压在整屏不透明的背景上，ROM 自己轮询 `$2002` 的 bit6，命中写 `$0300 = $A5`、超时写 `$00`。
  `ppu_diff.ps1` 里加断言。**验证过它真能抓住这个 bug**：把命中判定挪回优先级之后，
  这块 ROM 立刻写 `$00`。
- 冒烟测试新增 4 条**手柄端到端**断言（`Input.action_press` → `InputAdapter` → `Bus.Controller1`），
  顺带在系统快照里加了 `controller1` / `controller2` 两个键。

**验证**

- SMB：第 34 / 60 / 300 / 600 / 900 / 1200 / 1800 帧的画面和 fogleman/nes **按 RGB 逐像素完全一致**
  （第 900 帧就是演示模式里马里奥在 1-1 跑动）；RAM 差异只剩 1~2 字节（NMI 相位）。
- 按开始能进关卡：探针脚本按住 Start 之后，画面从标题画面变成 1-1 开局、TIME 从 400 开始倒数。
- 冒烟 **134/134**；CPU / PPU / APU / mapper 四套全过（PPU 那套多了精灵 0 的回归断言）。

### 2026-09-12 · SMB 噪音 / 速度偏快 / 选不了 2P

**现象**（用户）：SMB 声音有噪音；感觉跑得偏快，像 CPU 时序有问题；有个按键没法选 2P。

**排查与结论**

1. **速度**：写了 `tools/probe_perf.gd`（必须**带窗口**跑 —— 无头模式故意不节流）。
   原来模拟器是"每个显示帧跑一帧"，在 165Hz 屏上就是 2.7 倍速。改成按墙钟累积
   （`TargetFrameSeconds=1/60.0988`，见 §11.2）后实测 **60.23 / 59.92 NES 帧/秒**（真机 60.0988），
   并且 `Godot 累计 delta` 和墙钟对得上。
   > 顺带说明：这个环境里从命令行起的 Godot 窗口偶尔会被系统"饿"到（每秒一次 ~500ms 的停顿，
   > 和 vsync 开关、音频驱动 Dummy 都无关），那种时候帧数会掉到 30~45。这是环境问题，
   > 不是模拟器跑快了 —— 模拟器**只会比真机慢，不会比真机快**（累计器 + 每帧最多补 4 帧）。
2. **音频噪音**：喂快 2.7 倍 → `AudioStreamGenerator` 缓冲必然溢出 → 丢样本，听感就是"咔咔"的噪音。
   节流修好后正常运行时 `DroppedSamples` 恒为 0（`Write` 也改成只推装得下的那部分，不再整批丢）。
3. **APU 本身是对的**（花了力气证伪）：`--dump-audio` 导出 SMB 的 PCM，和 jsnes 的导出比 ——
   响度包络相关系数只有 0.66~0.75（各家混音曲线和音量本来就不同，没有可比性），
   但**过零率（音高）相关系数 0.965、最佳滞后 0 块**：同一段旋律、同一时刻，一个音都不差。
   比较脚本：`.ref/pcm-corr.js`（包络 + 过零率），`.ref/jsnes-audio.mjs`（带按键脚本的 jsnes 导出）。
4. **2P**：手柄 2 原来**根本没有映射**（`InputAdapter` 只填 `Controller1`）。现在补上
   `nes2_*` 动作（小键盘 + 第二个手柄设备），并把手柄 1 的 joypad 收窄到 device=0，避免两个手柄串台。
   另外查清一个"看着像 bug 其实不是"的行为：**标题画面上按住方向键时 Start 不生效**
   （要先松开方向再按 Start；按住不放的话游戏就一直停在标题画面）。
   这一点是拿 jsnes 对出来的：同一套按键脚本下，我这边和 jsnes 的 **RAM 逐字节相同（0 字节差异）、
   第 400 帧画面 sha256 相同**，所以是 SMB 自己的行为，不是模拟器的问题。
   顺带用 `--press` + `--dump-ram`/`--jsnes-ram.mjs` 做成了"按键 → 游戏状态"的差分手段。

**新增/改动**

| 位置 | 内容 |
|:---|:---|
| `script/EmulatorCore.cs` | 墙钟节流；`--press <键> <按下帧> [松开帧]` 测试钩子 |
| `script/NesAudioPlayer.cs` | `Write` 只推装得下的部分 + `DroppedSamples` 计数 |
| `script/InputAdapter.cs` | 手柄 1 / 手柄 2 两套映射，推 `Controller1` + `Controller2` |
| `project.godot` | `nes2_*` 动作（小键盘 8/2/4/6、1、3、+、0 + 手柄设备 1）；手柄 1 的 joypad 改成 device=0 |
| `ui/main.gd` + `lang/language.csv` | 快捷键窗口列出两个手柄的键位；补上"2 人游戏怎么选"的说明（中英） |
| `autoload/EmulatorService.cs` + `ui/main.gd` | 状态栏显示**模拟器自己的 NES 帧率**（快照新增 `nes_fps`），和显示帧率并排 —— "感觉快不快"一眼可见 |
| `tools/probe_perf.gd` | 带窗口量速度 / 卡顿 / 丢样本的探针（保留，排查性能用） |

**验证**

- 冒烟测试 **144/144**（新增 4 条手柄 2 断言、1 条新翻译断言、2 条帧率断言），带窗口那份也过。
- CPU / PPU / APU / mapper 四套差分测试全过（CPU 800 行逐行一致、PPU 逐像素一致、APU 频率对、mapper trace 一致）。
- SMB 音频：和 jsnes 过零率相关 0.965（lag 0）。
- SMB 输入：1P / 2P 两条按键脚本下，RAM 和画面与 jsnes 逐字节一致。
- 速度：`tools/probe_perf.gd` 实测 60.23 / 59.92 NES 帧/秒（目标 60.0988），`DroppedSamples` = 0。

### 2026-09-12 · 固定步长驱动 + 按参照实现补 APU 缺的东西

**1. 主场景改用 `_physics_process`（固定步长）驱动**

`_process` 的步长跟着显示刷新率走，Godot 主循环被渲染拖一下，模拟节奏就跟着抖 ——
同一台机器上实测到过"每秒一次约 500ms 停顿、帧率掉到 30~45"。改成物理步（固定 1/60 秒）之后：

| 配置 | 实测 |
|:---|:---|
| 默认（vsync 开） | 3.01 秒 → 181 帧 = **60.17 帧/秒**，>50ms 卡顿 **0 次** |
| vsync 关 | 3.01 秒 → 180 帧 = **59.80 帧/秒**，0 次 |
| vsync 关 + 音频停 | 3.00 秒 → 180 帧 = **59.90 帧/秒**，0 次 |

物理步长（16.667ms）和 NTSC 帧长（16.639ms）并不相等，所以内部仍按时间累积
（见 §11.2）：绝大多数步跑 1 帧，约每 607 步多跑 1 帧，长期平均贴着真机。
无头模式仍不节流，并把物理步频提到 1000，免得导帧要按真实时间等。

**2. 拿 jsnes（`src/papu/`）当参照，补齐 APU 缺的东西**

用户反馈"游戏开始后有背景音乐时还是有噪音"。对照 jsnes（`src/papu/`）、fogleman/nes、
FCEUX（`src/sound.cpp`）三家的 APU 实现逐条比对，找到并修掉五处：

| 问题 | 说明 / 影响 |
|:---|:---|
| **DMC 没使能时定时器还在走，移位器还会自己重新武装**（**持续嘶声的根因**） | `$4015` 关掉 DMC 之后，残留的移位器拿着最后一个字节反复"按位加 2 / 减 2"，DAC 以 DMC 速率一直抖 —— 输出永远回不到 0，听感就是持续的背景"嘶嘶"声。fogleman 与 jsnes 都在定时器入口 `if (!enabled) return`，FCEUX 把 `DMCSize` 清 0 让 DMA/移位器停下；另外"样本放完且不循环"时也不能再重新装载 8 位。修完之后 SMB 的静音段真的回到 **RMS 0.0000**（和 jsnes 一致） |
| **噪声通道慢了一倍** | 周期表（4/8/16…4068）的单位是 **CPU 周期**（$0 → 447kHz），但我把噪声和脉冲一起放在"每 2 个 CPU 周期"那一组里了 —— 噪声整体低一个八度。SMB 用噪声打鼓，听感就是"闷响/噪音"。修完 `apu-noise` 的过零数从 6791 涨到 **13936**，正好翻倍 |
| **没有抗混叠（盒式滤波）** | 原来只在出样本的那一刻取瞬时值：44.1kHz 点采样方波会严重混叠，折回来的频率和乐音无关 —— 这正是"发毛、有噪音"的主要来源。现在**每个 CPU 周期累加各通道输出、出样本时取平均**（jsnes 同款做法） |
| **三角波不活动时被归零** | 真机 DAC 是"保持当前电平"；归零会让每个音符结束都跳变一下，低音声部一下一下"咔"。现在不活动时定序器停住、电平保持 |
| **长度计数器装载没看使能位** | 真机上通道被禁用时写 $4003/$4007/$400B/$400F **不装载长度计数器**（jsnes 也这么做）。顺带发现自己写的 `apu_test` ROM 顺序不对（先写长度后使能），已改成"先使能" |

另外补了**直流消除器**（一阶高通，反馈 1/1024，jsnes 同款）：真机 DAC 是单极性的，
直接播会带恒定直流、吃动态范围、开机还会"咚"一下。加完之后 `apu-silent.nes` 仍然精确输出 0。

**3. 状态栏太长顶出屏幕**

`StatusBar` 的 Label 加 `autowrap_mode = 3`（按词折行），内容也改成**两行**：
第一行"文件名 | Mapper"，第二行"NES 帧率 | 显示帧率 | 帧号 | 运行状态"。
文件名再长也只会自己折行，不会顶出窗口。

**验证**

- `apu_test.ps1` **5 个用例全过**：静音精确 0、脉冲 440.6 Hz、三角波 220.4 Hz、
  噪声过零 41814（宽带）、`apu-dmc-off` 开头峰值 14904 而关掉之后尾段峰值 **0**。
- **回归测试有效性验证过**：把"自己重新武装移位器"的行为临时加回去，`apu-dmc-off` 立刻报
  `[FAIL] 关掉 DMC 之后还有持续抖动（尾段峰值 120）`，而且静音/脉冲/三角/噪声四个用例也一起挂了 ——
  正好复现了"背景嘶声盖住整段音乐"的现象。
- 和 jsnes 比 SMB 的游戏内音频：**过零率（音高）相关 0.975、最佳滞后 0 块**、
  响度包络相关 **0.785**；静音段逐块对比都是 0.0000，两边一致。
- 冒烟测试 **144/144**（新增状态栏 3 条：有换行符 / 开了自动换行 / 超长文字真的折成多行），
  CPU / PPU / mapper 三套差分测试全过。
- 对照工具：`.ref/pcm-corr.js`（包络 + 过零率相关）、`.ref/pcm-blocks.js`（逐块 RMS/峰值/过零）、
  `.ref/jsnes-audio.mjs`（可带按键脚本的 jsnes 音频导出）。


---

## 16. 差分排查方法与工具链（画面/状态问题的操作手册）

这一节把《Mighty Final Fight》底部状态栏那次排查完整记下来 —— 最后定位到"纵向卷轴基准规则"，
过程中沉淀了一套工具。以后遇到"某个 ROM 画面对不上/状态不对"，照这个流程走。

### 16.1 命令行开关（`script/EmulatorCore.cs`）

| 开关 | 用途 |
|:---|:---|
| `--press <键> <按下帧> [松开帧]` | 脚本化按键（无头差分测试用） |
| `--press-file <路径>` | **重放录制的操作** —— 有了它，排查不用再让人手动玩一遍 |
| `--log-input <路径>` | **录制**每个按键变化（每行 `帧 按键位(hex)`） |
| `--ram-trace <路径>` | 每帧追加 2KB 主 RAM（和参照实现 / FCEUX 逐帧对比用） |
| `--log-mapper <路径>` | 映射器寄存器写：`帧 扫描线 寄存器 值` |
| `--log-ppu <路径>` | PPU 寄存器写：`帧 扫描线 寄存器 值` |
| `--log-ppu-scroll` | 再加每条关键扫描线的卷轴状态（`t` / fineX / 基准行） |
| `--log-apu <路径>` | `$4015` 读写 + 各通道长度计数器 |
| `--log-sprites <路径>` | 每行精灵评估数（eval/drawn/limit）+ 完整 OAM 表 |
| `--trace-from-frame <N>` + `--trace-console <N>` | 从第 N 帧起打印 N 条指令 |
| `--dump-frame-indices` / `--dump-ram` / `--dump-audio` | 帧索引 / RAM / PCM 导出 |

按键位（与 fogleman、jsnes 一致）：bit0=A、1=B、2=Select、3=Start、4=Up、5=Down、6=Left、7=Right。

**界面上按 `Q`**：把当前帧的完整快照写到 `--log-input` 所在目录 ——
`.idx`（画面）、`.nt`（4KB 名称表）、`.chr`（当前映射出来的 8KB CHR）、`.oam`、`.pal`、`.ram`。
这一套比截图有用得多：能离线把那一帧原样重画出来。

### 16.2 对照侧工具（`.ref/`，不进仓库分发）

| 工具 | 用途 |
|:---|:---|
| `fceux-state-parse.js` | **解析 FCEUX 即时存档**（`FCSX` 头 + zlib 压缩段），取 2KB RAM |
| `fceux-mmc3regs.js` | 从存档里按段名读 `REGS`（R0-R7），核对 CHR/PRG bank |
| `chr-check.js` | 把"当前映射出来的 8KB CHR"逐 1KB 对回 ROM 文件，确认映射没错 |
| `chr-sheet.js` / `tile-by-bank.js` | 按 bank 打印图块表 / 同一图块跨 bank 对比 —— 定位"字体在哪个 bank" |
| `find-nametable.js` | 从存档里按**结构**取出名称表并逐行比对 |
| `ram-trace-diff.js` / `trace-vs-fceux.js` | 逐帧 RAM 对比，自动排除"两边初值不同"的未初始化字节 |
| `make-fceux-ramtrace.js` | 由录制的按键生成 FCEUX Lua 脚本（重放 + 逐帧写 RAM） |
| `frame-shift.js` | 两帧做上下平移，量化"整体上移/下移"类问题 |
| `band-compare.js` | 把两帧的某条横带拼成一张图对比 |
| `render-bank.js` | 用指定 bank 把名称表渲染成图 |
| `pcm-corr.js` / `pcm-blocks.js` / `jsnes-*.mjs` | 音频包络与过零相关、逐块 RMS；jsnes 侧的帧/RAM/音频导出 |

### 16.3 案例：底栏显示成"顶部 HUD 的内容"

**症状**：顶部 HUD 与场景都正常，唯独底部状态栏显示成顶部 HUD 的图块，还跟着场景一起滚动。

**排查路径**（每一步都拿字节级证据，不靠肉眼）：

1. `--log-ppu` 看清分屏写法：扫描线 32 写 `$2005 = X,$22`（场景纵向 34）、扫描线 194 写
   `$2005 = $00,$00`（底栏纵向 0）；全程 `$2000` 的名称表位恒为 0 —— **两段读的是同一块名称表、
   同一段行**，区分它们的是**扫描线 194 那次中断里换 CHR bank**（`$8001 = $78/$7A`）；
2. `--log-mapper`（带扫描线）确认这一写确实落在第 194 行，时机没错；
3. `Q` 快照 + `chr-check.js` 确认底栏那一刻读到的 CHR 就是 ROM 的 `$78/$79/$7A/$7B`；
   `tile-by-bank.js` 搜出"HUD 字体 bank = `$78`"（全库唯一同时含 L/E/0 的 bank）；
4. 名称表第 26 行是 `68 64 70 64 68`（= L-E-V-E-L），**数据是对的**；
5. 于是只剩"这一行落在屏幕第几行"。在渲染循环里临时打探针看 `worldY`：
   底栏基准若按"写入行"算，第 26 行（worldY 208）被推到屏幕 y=403 —— **屏幕外**，
   底栏只能显示名称表第 0-5 行，正是顶部 HUD 的内容。

**最终规则**（写在 `Ppu._scrollBaseLine` 的注释里）：

```
worldY = 粗Y*8 + 精细Y + (扫描线 - 基准行)

基准行：
  纵向写非 0（场景分屏）→ 基准 = 写入行 + 1
  纵向写  0 （底栏分屏）→ 基准 = 0（从名称表顶行算）
```

两条各有客观验证：场景那条对应"帧 1600 与参照实现逐像素 0 差异"；
底栏那条对应"LEVEL/EXP 落在屏幕 208-215 行，与 FCEUX 截图一致"。

### 16.4 两条方法论教训（都踩过）

1. **别把"1KB bank 视图"和"2KB bank 视图"混用。** MMC3 的 `R0` 覆盖 2KB（`$0000-$07FF`），
   它由两个 1KB bank 组成：图块 `$68` 的地址是 `$680`，落在**第二个** 1KB bank 里。
   我曾拿"1KB bank `$79` 的图块 `$68`"去核对，读到空数据，差点误判成映射错了。
2. **不要用"在参照数据里搜索最像我的一段"来证明两边一致** —— 那是循环论证。
   FCEUX 存档里的段是**有名字的**（`NTAR` / `REGS` / `RAM` …），按结构取出来才算证据。
