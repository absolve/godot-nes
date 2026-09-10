下面是一份可直接保存为 `godot-nes-emulator-plan.md` 的基本开发计划。目标是在 **Godot 4.x** 中实现一个能运行《超级马里奥兄弟》的 NES 模拟器，并逐步扩展兼容性。

---

# Godot NES 模拟器基本开发计划

## 0. 目标与范围

### 0.1 最终目标
- 在 Godot 4.x 中加载 `.nes` ROM，运行 NES 游戏。
- 优先跑通《超级马里奥兄弟》：标题画面 → 1-1 关 → 可操作 → 有声音。
- 支持基础 Mapper：NROM（000）、MMC1（001）、UxROM（002）、CNROM（003）、MMC3（004）。
- 提供简单调试 UI：FPS、CPU 寄存器、PPU scanline/dot、内存查看。

### 0.2 初期非目标
- 不追求 100% 硬件兼容。
- 不实现全部 Mapper 和非法指令。
- 不追求主时钟级精度，先做到 **CPU 周期 + PPU dot 级**。

---

## 1. 技术选型与总体架构

### 1.1 技术选型
| 项目 | 选择 | 理由 |
|:---|:---|:---|
| 引擎 | Godot 4.3+ | 场景/UI/音频方便 |
| 核心语言 | **C#** | 性能远好于 GDScript，适合热路径 |
| UI/胶水 | GDScript 或 C# | 调试界面、ROM 选择器 |
| 渲染 | `ImageTexture` + `Image.SetData` | 256×240 帧缓冲，每帧更新 |
| 音频 | `AudioStreamGenerator` | 推送 APU 生成的样本 |
| 输入 | Godot `InputMap` | 映射到 NES 手柄 |
| 测试 | `nestest.nes`、`ppu_vbl_nmi` 等 | 硬件行为验证 |

> 如果坚持纯 GDScript，可先做原型，但 PPU/CPU 热路径大概率需要迁移到 C# 或 GDExtension。

### 1.2 Godot 场景树

```text
Main (Node)
├── EmulatorHost (Node)          # 驱动 Console.StepFrame()
├── Display (TextureRect)        # 显示 256x240 纹理
├── AudioPlayer (AudioStreamPlayer)
├── InputAdapter (Node)          # Godot 输入 -> NES 手柄
├── DebugUI (CanvasLayer)
│   ├── CpuPanel
│   ├── PpuPanel
│   └── FpsLabel
└── RomLoader (FileDialog / 按钮)
```

### 1.3 核心模拟器结构

```text
Console
├── CPU (6502)
├── PPU (2C02)
├── APU (2A03)
├── Bus
├── Cartridge
│   └── Mapper
└── Controller x2
```

核心模拟器应与 Godot 节点解耦：`Console` 是纯 C# 类，Godot 只负责驱动、显示和音频输出。

---

## 2. 分阶段开发计划

### 阶段 0：项目准备（1 天）
**任务**
- 创建 Godot C# 项目，建立目录结构。
- 定义接口：`IBus`、`IRenderer`、`IAudioSink`、`IInputSource`。
- 实现 iNES ROM 解析：读取 header，识别 PRG/CHR 大小、Mapper 号、镜像模式。

**验收**
- 能打印 ROM 信息，例如：`Mapper 0, PRG 32KB, CHR 8KB, Horizontal Mirroring`。

---

### 阶段 1：CPU 6502 核心（1–2 周）
**任务**
- 实现寄存器：A、X、Y、SP、PC、P。
- 实现 56 条官方指令与寻址模式。
- 实现中断：NMI、IRQ、RESET。
- 实现周期计数，支持“指令消耗多个 CPU 周期”。

**验收**
- 运行 `nestest.nes`，与官方日志逐行对比 PC、A、X、Y、P、SP、CYC。
- 要求前 5000 行完全一致。

**里程碑 M1：CPU 通过 nestest。**

---

### 阶段 2：总线与卡带 / Mapper 0（3–5 天）
**任务**
- CPU 内存映射：2KB RAM 镜像、PPU 寄存器、APU 寄存器、手柄、卡带空间。
- Cartridge：加载 PRG/CHR，NROM 映射。
- PPU 寄存器先做桩，能读写不崩溃。

**验收**
- CPU 能从卡带读取指令并执行简单循环。
- 能响应 RESET 向量。

---

### 阶段 3：PPU 基础渲染（2–4 周）
**任务**
- PPU 寄存器 `$2000-$2007`。
- VRAM、Pattern Table、Name Table、Palette。
- 背景渲染：scanline/dot 循环，8 像素移位寄存器。
- VBlank 标志与 NMI 触发。
- 输出 256×240 帧缓冲，用 `ImageTexture` 显示。

**验收**
- 显示静态背景、正确调色板。
- 运行《超级马里奥》标题画面。

**里程碑 M2：能看到游戏标题画面。**

---

### 阶段 4：PPU 精灵、滚动与 OAM DMA（2–3 周）
**任务**
- OAM：64 个精灵，每 scanline 最多 8 个。
- 精灵 0 命中、精灵溢出。
- 滚动寄存器与 Loopy 寄存器。
- `$4014` OAM DMA。
- 支持 8×8 / 8×16 精灵。

**验收**
- 马里奥精灵显示正常，背景可滚动，状态栏固定。
- 能进入 1-1 关。

**里程碑 M3：可玩《超级马里奥》第一关（无声音）。**

---

### 阶段 5：输入与游戏循环（1–2 天）
**任务**
- 手柄读取 `$4016/$4017`。
- Godot `InputMap` 映射方向、A、B、Start、Select。
- 固定 60 FPS 帧循环，`_Process` 中运行整帧模拟。

**验收**
- 能控制马里奥移动、跳跃、进入管道。

**里程碑 M4：基础可玩版完成。**

---

### 阶段 6：APU 音频（1–2 周）
**任务**
- 5 个通道：Pulse×2、Triangle、Noise、DMC。
- 帧计数器、长度计数器、包络、扫频。
- 混音后推送到 `AudioStreamGenerator`。
- 处理 DMC DMA 对 CPU 的暂停。

**验收**
- 能听到背景音乐和音效。
- 音频不爆音、延迟可接受。

**里程碑 M5：带声音运行《超级马里奥》。**

---

### 阶段 7：Mapper 扩展与兼容性（2–4 周）
**任务**
- 实现 Mapper 1、2、3、4。
- MMC3 扫描线 IRQ。
- 用测试 ROM 验证：`ppu_vbl_nmi`、`sprite_hit_tests`、`mmc3_test`。

**验收**
- 支持《塞尔达传说》《魂斗罗》《超级马里奥兄弟 3》等。

**里程碑 M6：多 Mapper 兼容版。**

---

### 阶段 8：调试、优化与发布（2–4 周）
**任务**
- 性能优化：C# 热路径、数组池、避免 GC、`Span<byte>`。
- 调试器：CPU/PPU 状态、内存查看、断点、逐帧。
- 保存状态、ROM 选择器、设置界面。
- 导出 Windows/Linux/macOS 版本。

**验收**
- 主流游戏稳定 60 FPS，无明显音频延迟。

---

## 3. 时间估算总表

| 阶段 | 内容 | AI 辅助生成 | 人工调试 | 小计 |
|:---|:---|:---|:---|:---|
| 0 | 项目准备 | 0.5 天 | 0.5 天 | 1 天 |
| 1 | CPU 6502 | 1 天 | 3–7 天 | 1–2 周 |
| 2 | 总线 + Mapper 0 | 0.5 天 | 2–3 天 | 3–5 天 |
| 3 | PPU 基础 | 2 天 | 1–3 周 | 2–4 周 |
| 4 | PPU 精灵/滚动 | 2 天 | 1–2 周 | 2–3 周 |
| 5 | 输入 | 0.5 天 | 0.5 天 | 1–2 天 |
| 6 | APU | 1 天 | 1–2 周 | 1–2 周 |
| 7 | Mapper 扩展 | 2 天 | 2–4 周 | 2–4 周 |
| 8 | 优化/发布 | 2 天 | 2–4 周 | 2–4 周 |

**基础可玩版（阶段 0–5）**：约 **1.5–3 个月**。  
**完整版（阶段 0–8）**：约 **4–6 个月**。  
AI 能大幅压缩编码时间，但调试、测试 ROM 对比、性能优化仍占大头。

---

## 4. 关键风险与对策

| 风险 | 对策 |
|:---|:---|
| Godot 主循环不适合周期精确 | 在 `_Process` 内跑完整帧模拟，用 `Stopwatch` 控制节奏 |
| GDScript 性能不足 | 核心用 C#，GDScript 只做 UI |
| PPU 太复杂 | 先背景，后精灵，再滚动；用 FCEUX 对比 |
| 音频延迟/爆音 | 预填充 `AudioStreamGenerator` 缓冲，稳定推送样本 |
| AI 生成代码有隐藏 bug | 每阶段用测试 ROM 验收，不通过不进入下一阶段 |
| Mapper 兼容性爆炸 | 按流行度实现，优先 0/1/2/3/4 |

---

## 5. 推荐目录结构

```text
godot-nes/
├── project.godot
├── scenes/
│   ├── Main.tscn
│   └── DebugUI.tscn
├── scripts/
│   ├── EmulatorHost.cs
│   ├── Display.cs
│   ├── AudioPlayer.cs
│   └── InputAdapter.cs
├── core/
│   ├── Console.cs
│   ├── CPU/
│   │   ├── Cpu.cs
│   │   ├── Opcodes.cs
│   │   └── Addressing.cs
│   ├── PPU/
│   │   ├── Ppu.cs
│   │   ├── Registers.cs
│   │   └── Renderer.cs
│   ├── APU/
│   │   ├── Apu.cs
│   │   └── Channels.cs
│   ├── Bus/
│   │   ├── Bus.cs
│   │   └── MemoryMap.cs
│   ├── Cartridge/
│   │   ├── Cartridge.cs
│   │   └── Mappers/
│   └── Input/
│       └── Controller.cs
├── tests/
│   ├── nestest.nes
│   └── roms/
└── README.md
```

---

## 6. 开发工作流与 AI 使用建议

1. **垂直切片优先**：先做“加载 ROM → CPU → 最小 PPU → 显示标题”，不要一开始就写完整 APU。
2. **每个模块独立测试**：CPU 用 `nestest`，PPU 用测试 ROM，Mapper 用对应测试 ROM。
3. **AI 生成骨架，人类验证行为**：用 DeepSeek Harness 生成类结构、指令表、寄存器定义，但周期和边界条件必须人工核对。
4. **参考开源实现**：
   - FCEUX：看调试器输出、Mapper 实现、时序细节。
   - fogleman/nes：看清晰的组件划分和 Go 语言实现。
5. **持续对比**：用 FCEUX 运行同一 ROM，对比画面、音频、CPU 状态。

---

## 7. 第一个可运行里程碑

**目标**：加载 `Super Mario Bros.nes`，显示标题画面，按 Start 进入 1-1，能控制马里奥移动跳跃，60 FPS。

建议按此顺序推进：

```text
ROM 解析 → CPU nestest 通过 → 总线 + Mapper 0 → PPU 背景 → VBlank/NMI
→ 标题画面 → OAM DMA + 精灵 → 滚动 → 输入 → 可玩 1-1
→ APU 声音 → 多 Mapper
```

只要守住每个阶段的验收标准，这个项目就能稳步推进，不会陷入“什么都写了一点但跑不起来”的状态。