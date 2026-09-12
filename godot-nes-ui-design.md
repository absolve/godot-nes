# godot-nes 主界面与调试窗口设计

目标：**主窗口只留菜单 + 游戏画面**，CPU / PPU / 内存等调试信息放到 Godot 4 的 `Window` 里
单独开 OS 窗口显示；主界面用 GDScript 写，模拟器核心继续用 C#。

本文里的结论都是在这台机器（Godot 4.7.2 mono + .NET 10）上跑探针脚本实测出来的，
不是照文档推测的；每条都标了证据。

> **状态：本文的设计已全部实施**（见 §10 实施记录），并把实施过程中实测到的偏差与坑补了进去。

---

## 0. 先回答两个问题

### Q1：主界面用 GDScript，会不会和 C# 冲突？

**不会，可以放心混用。** 同一个工程、同一个场景树里 GDScript 和 C# 节点可以任意搭配，
GDScript 能直接调用 C# 的 public 方法、读写 C# 属性、连接 C# 的 `[Signal]`。
但有 **4 条硬边界**，踩上去就是「方法明明写了却调不到」，见 §1。

### Q2：用 `Window` 单独开窗口，需要做什么？

**必须显式把 `display/window/subwindows/embed_subwindows` 设成 `false`。**
这个设置的默认值是 **`true`**（子窗口嵌在主窗口里画），不关掉的话 `Window` 节点只是
主窗口内部的一块区域，不是独立窗口。已经在本工程的 `project.godot` 里打开了：

```ini
[display]
window/subwindows/embed_subwindows=false
```

实测证据（同一份 `Window` 探针脚本）：

| 运行方式 | `DisplayServer.has_feature(SUBWINDOWS)` | `embed_subwindows` | 是否真的独立弹出 |
|:---|:---|:---|:---|
| 无头 `--headless` | `false` | `true` | `false`（无头没有窗口系统） |
| 带窗口，未设置 | `true` | `true` | **`false`（被嵌进主窗口）** |
| 带窗口，设为 `false` | `true` | `false` | **`true`** |

---

## 1. 实测出来的语言边界

用探针脚本（`get_node("/root/EmulatorService")` 然后逐个试）跑出来的结果：

| 能力 | 结论 | 实测 |
|:---|:---|:---|
| GDScript 调 C# public 方法 | ✅ 可以 | `svc.has_method("LoadRom") == true`，`svc.LoadRom(path)` 正常返回 `false` |
| C# 方法参数/返回值是 Variant 兼容类型 | ✅ 才可见 | `Display.Present(byte[])` → `has_method == true` |
| **C# 方法参数含非 Variant 类型** | ❌ **完全不可见** | `NesAudioPlayer.Write(ReadOnlySpan<float>)` → `has_method == false`；`InputAdapter.PushTo(NesConsole)` → 同样 `false` |
| C# 属性（类型 Variant 兼容） | ✅ 自动暴露 get/set，**不需要 `[Export]`** | `svc.CurrentRomPath` 直接读到 `''` |
| **C# 属性（类型是纯 C# class）** | ❌ **不进属性表** | `svc.get("Console")` → `<null>` |
| **属性的 `private set`** | ⚠️ **挡不住 GDScript 写入** | `svc.CurrentRomPath = "..."` 写成功后读回 `'被 GDScript 写坏了'` |
| C# `[Signal]` → GDScript 回调 | ✅ 参数正常 | `RomLoadFailed.connect(...)` 收到 `(path, error)` 两个参数 |
| GDScript 实例化 C# 脚本的场景 | ✅ 可以 | `load("res://scene/main.tscn").instantiate()`，读 `audio.MixRate` 得到 `44100.0` |
| GDScript 调 C# **static** 方法 | ⚠️ 名字在方法表里但没有实例语义 | `has_method("ToFileSystemPath") == true`（它是 static） |

**由此推出的核心约束：**

1. **纯 C# 类型（`NesConsole`/`Cpu`/`Ppu`/`Cartridge`）对 GDScript 完全不可见。**
   GDScript 拿不到 `Console` 对象，也就没法 `EmulatorService.Console.Ppu.Ctrl` 这样链式访问。
   调试数据必须由 C# 主动「拍快照」成 `Dictionary` / `PackedByteArray` 再交出去。
2. **任何要给 GDScript 调的 C# 方法，签名里只能出现 Variant 兼容类型**
   （`int/float/bool/String/Dictionary/Array/PackedXxxArray/Vector2…`）。
   否则这个方法根本不会进方法表 —— 而且**不报错、不警告**，只是调不到。
3. **只读状态不要用 C# 属性暴露**，`private set` 拦不住 GDScript。要么用方法返回，
   要么在 GDScript 侧自觉（建议前者）。

---

## 2. 分工原则

| 层 | 语言 | 理由 |
|:---|:---|:---|
| 模拟器核心 | **C#**（`core/`，不变） | 热路径，性能 |
| 每帧热路径：跑一帧 + 采样输入 + 上传纹理 | **C#**（`EmulatorCore`） | **帧缓冲（245 KB）绝不能跨语言传递**，必须一次调用在 C# 内部完成 |
| 菜单 / 窗口 / 对话框 / 状态栏 | **GDScript** | 改动频繁、免编译即时生效，频率低（10~20 Hz） |
| 窗口里的「重数据」控件（画面预览） | **C#** 节点 | 让数据留在 C# 里，GDScript 只负责摆放它 |

一句话：**控制流和 UI 归 GDScript，热路径和数据归 C#，一次跨语言调用跑完整帧。**

---

## 3. 数据通道：快照 API

### 3.1 C# 侧（`autoload/EmulatorService.cs` 增加）

全部返回 Variant 兼容类型，GDScript 才看得见：

```csharp
// 系统级：帧号、暂停状态、ROM 信息、FPS
public Godot.Collections.Dictionary GetSystemSnapshot() => new()
{
    { "frame",    Console.FrameCount },
    { "paused",   Console.Paused },
    { "rom_name", CurrentRomPath.Length > 0 ? Path.GetFileName(CurrentRomPath) : "" },
    { "rom_desc", CurrentRomDescription },
    { "last_error", LastError },
    { "mapper",   Console.Cartridge?.MapperNumber ?? -1 },
    { "mapper_name", Console.Cartridge?.Mapper.Name ?? "" },
    { "fps",      Engine.GetFramesPerSecond() },
};

// CPU：寄存器 + 标志位拆开，方便 GDScript 直接显示
public Godot.Collections.Dictionary GetCpuSnapshot() => new()
{
    { "pc", Console.Cpu.PC }, { "a", Console.Cpu.A }, { "x", Console.Cpu.X },
    { "y", Console.Cpu.Y },  { "sp", Console.Cpu.SP }, { "p", Console.Cpu.P },
    { "cycles", Console.Cpu.Cycles },
    { "n", Console.Cpu.GetFlag(StatusFlags.Negative) },
    { "v", Console.Cpu.GetFlag(StatusFlags.Overflow) },
    /* … 其余标志位同理 */
    { "unimplemented_opcode", Console.UnimplementedOpcode ?? -1 },
};

// PPU：寄存器 + 状态位
public Godot.Collections.Dictionary GetPpuSnapshot() => new() { /* ctrl/mask/status/vram_addr/scanline/dot … */ };

// 每个域一个总入口，GDScript 一次调用全拿到（省的来回复制）
public Godot.Collections.Dictionary GetDebugSnapshot() => new()
{
    { "system", GetSystemSnapshot() },
    { "cpu", GetCpuSnapshot() },
    { "ppu", GetPpuSnapshot() },
};
```

窗口里要画图（pattern table、nametable、调色板色块）时，**不要让 GDScript 拿 `PackedByteArray` 自己画**，
而是提供一个 C# 的 `TextureRect` 子类节点（例如 `script\PatternTablePreview.cs`），
它自己去读 `EmulatorService.Instance.Console.Ppu`，只给 GDScript 暴露一个无参 `Refresh()` —— 数据不出 C#。

### 3.2 GDScript 侧：`DebugHub`（autoload）

```gdscript
# autoload/debug_hub.gd
extends Node

signal snapshot_updated(snapshot: Dictionary)

const PULL_INTERVAL := 1.0 / 12.0   # 12 Hz，UI 够用，跨语言开销可忽略

var snapshot: Dictionary = {}
var _timer := 0.0
var _consumers := 0

func _process(delta: float) -> void:
    if _consumers == 0:            # 没有窗口开着就完全不拉，零开销
        return
    _timer += delta
    if _timer < PULL_INTERVAL:
        return
    _timer = 0.0
    snapshot = EmulatorService.GetDebugSnapshot()   # 每 83ms 一次跨语言调用
    snapshot_updated.emit(snapshot)

func register_consumer() -> void:
    _consumers += 1

func unregister_consumer() -> void:
    _consumers = maxi(0, _consumers - 1)
```

要点：**只有窗口可见时才拉数据**。窗口全关时，调试开销精确为 0。

---

## 4. 主界面结构

```text
Main (Node)                          ← ui/main.gd（GDScript，场景根）
├── Ui (CanvasLayer)
│   └── Root (VBoxContainer)         ← 铺满整个窗口
│       ├── MenuBar                  ← 顶部菜单
│       │   ├── FileMenu (PopupMenu)
│       │   ├── EmulationMenu
│       │   ├── ViewMenu
│       │   ├── DebugMenu
│       │   └── HelpMenu
│       └── ScreenArea (Control)     ← size_flags_vertical = EXPAND_FILL
│           └── Display (TextureRect)  ← C# 脚本，256x240 帧缓冲上屏
├── DebugWindows (Node)              ← 所有独立窗口的父节点
│   ├── CpuWindow (Window .tscn)
│   ├── PpuWindow (Window .tscn)
│   ├── MemoryWindow (Window .tscn)  ← 以后
│   └── LogWindow (Window .tscn)     ← 以后
├── EmulatorCore (Node)              ← C#：每帧 Step + 采样输入 + 上传纹理
├── NesAudioPlayer (AudioStreamPlayer) ← C#
└── (状态栏可选：底部一条细的 HBox，放 ROM 名 + FPS + 运行状态)
```

### 4.1 GDScript 主脚本

```gdscript
# ui/main.gd
extends Node

@onready var _core: Node = $EmulatorCore

func _process(_delta: float) -> void:
    # 每帧就这么一次跨语言调用，内部完成 step + 输入采样 + 纹理上传
    _core.StepAndPresent()
```

C# 侧对应：

```csharp
// script/EmulatorCore.cs（由现有 EmulatorHost.cs 改造）
public partial class EmulatorCore : Node
{
    /// <summary>给 GDScript 的唯一热路径入口。帧缓冲不跨语言。</summary>
    public void StepAndPresent()
    {
        InputAdapter?.PushTo(Console);   // 注意：参数是 C# 类型，所以这个方法是 C# 内部调用
        Console.StepFrame();
        if (Console.Ppu.TakeFrameReady()) Display?.Present(Console.FrameBuffer);
    }
}
```

> `PushTo(NesConsole)` 因为参数是非 Variant 类型，GDScript 本来就调不到，
> 正好强制它留在 C# 内部 —— 这也是为什么输入采样要放在 `StepAndPresent` 里面。

### 4.2 顶部菜单

用 Godot 4 内置的 `MenuBar`（子节点是 `PopupMenu`，名字即菜单标题），不要自己画按钮。
实测 `MenuBar.new()` + `add_child(PopupMenu)` + `set_menu_title(0, "文件")` → `get_menu_count() == 1` 正常。

| 菜单 | 条目（括号内为快捷键） |
|:---|:---|
| 文件 | 打开 ROM… (O)、最近打开 ▸、退出 (Esc) |
| 模拟 | 复位 (R)、暂停/继续 (P)、单步指令 (F10)、快进 (Tab)、静音 |
| 视图 | CPU 窗口 (F2)、PPU 窗口 (F3)、内存窗口 (F4)、日志窗口 (F5)、显示缩放 (1x/2x/3x/自适应)、全屏 (F11) |
| 调试 | 显示状态栏、帧快照存 PNG、导出执行日志 |
| 帮助 | 快捷键、关于 |

实现要点：

- 菜单项用 `add_item(label, id)` + `id_pressed` 信号处理，`set_item_checked` 表示窗口开关状态。
- 快捷键建议用 `PopupMenu.set_item_shortcut()` 绑定 `InputEventKey`；全局快捷键（窗口开关）也可以
  沿用现在的 `project.godot [input]` 动作 + `_unhandled_input`。
- `MenuBar.prefer_global_menu` 默认是 `true`（实测）。Windows 上没有系统级菜单栏所以还是画在窗口内，
  但 **macOS 上会跑到系统菜单栏**，两端外观差异较大；想统一外观就显式设 `false`。

---

## 5. 独立调试窗口

### 5.1 每个窗口一个 `.tscn` + 一个 GDScript

```
ui/windows/cpu_window.tscn   根节点 Window，脚本 cpu_window.gd
ui/windows/ppu_window.tscn   根节点 Window，脚本 ppu_window.gd
ui/debug_window.gd           基类脚本（class_name DebugWindow）
```

### 5.2 基类处理通用逻辑

```gdscript
# ui/debug_window.gd
extends Window
class_name DebugWindow

func _ready() -> void:
    close_requested.connect(hide)          # 关闭 = 隐藏，不 free，保留位置和状态
    visibility_changed.connect(_on_visibility_changed)
    DebugHub.snapshot_updated.connect(_on_snapshot)
    hide()

func _on_visibility_changed() -> void:
    if visible:
        DebugHub.register_consumer()
        _refresh(DebugHub.snapshot)        # 打开时立刻刷一次，避免显示空数据
    else:
        DebugHub.unregister_consumer()

func _on_snapshot(snapshot: Dictionary) -> void:
    if visible:
        _refresh(snapshot)

func _refresh(_snapshot: Dictionary) -> void:
    pass                                    # 子类覆写
```

### 5.3 Window 属性建议

| 属性 | 值 | 说明 |
|:---|:---|:---|
| `title` | `"CPU"` / `"PPU"` | |
| `size` / `min_size` | `Vector2i(440, 340)` / `Vector2i(260, 180)` | |
| `transient` | `true`（默认） | 浮在主窗口上方 |
| `exclusive` | `false` | 不要挡住主窗口输入 |
| `wrap_controls` | `true` | 内容随窗口缩放 |
| `unresizable` | `false`（默认） | |
| `visible` | `false` | 初始隐藏，由视图菜单打开 |

- 首次显示调用 `popup_centered()`；多个窗口同时开时按 `position` 错开，避免叠在一起。
- **无头 / 不支持子窗口的平台要能降级**：实测无头下 `has_feature(SUBWINDOWS) == false`，
  此时窗口逻辑不能崩。做法是把「取数据 / 格式化文本」和「窗口显示」分开，
  这样自动化测试里也能验证数据逻辑。

### 5.4 窗口内容

| 窗口 | 内容 |
|:---|:---|
| CPU | PC/A/X/Y/SP/P（标志位用彩色小方块 N V - B D I Z C）、周期数、当前指令反汇编、栈顶 8 字节、未实现指令提示 |
| PPU | PPUCTRL/PPUMASK/PPUSTATUS 的位域展开、当前 scanline/dot、VRAM 地址、OAMADDR、32 字节调色板色块、两张 pattern table 预览（C# 控件）、名称表预览 |
| ROM 信息 | 文件路径/大小、格式（iNES 1.0 / NES 2.0）、mapper 与家族名、子 mapper、程序块/图案块大小与 bank 数、WRAM/NVRAM、镜像、制式、平台、电池、trainer、向量表、CRC32、MD5、解析提示 |
| 内存 | 十六进制查看器 + 地址跳转（阶段 7 之后再做） |
| 日志 | 指令追踪列表，可暂停/过滤（阶段 8） |

> ROM 信息窗口的数据只在装载 ROM 时才变，所以它不走 12 Hz 的快照，而是由 DebugHub 监听
> C# 的 `RomLoaded` 信号后重新拉一次并缓存进快照里。

---

## 6. 改动清单

| 文件 | 动作 |
|:---|:---|
| `project.godot` | ✅ **已改**：加 `window/subwindows/embed_subwindows=false`；再加窗口快捷键动作 |
| `autoload/EmulatorService.cs` | 增加 `GetSystemSnapshot/GetCpuSnapshot/GetPpuSnapshot/GetDebugSnapshot`（C#） |
| `autoload/debug_hub.gd` | **新增**（GDScript autoload，按需拉快照并广播） |
| `script/EmulatorHost.cs` | 改造为 `script/EmulatorCore.cs`，只留 `StepAndPresent/Reset/LoadRom` |
| `script/EmulatorUi.cs` | **删除**（C# 调试面板拆到 GDScript 窗口里） |
| `ui/main.gd` | **新增**：主界面控制流 + 菜单 |
| `ui/debug_window.gd` | **新增**：窗口基类 |
| `ui/windows/cpu_window.tscn/.gd` | **新增** |
| `ui/windows/ppu_window.tscn/.gd` | **新增** |
| `script/PatternTablePreview.cs` | **新增**：PPU 窗口里的 pattern table 预览控件（数据不出 C#） |
| `scene/main.tscn` | 根节点脚本换成 `ui/main.gd`，加 `MenuBar`、`DebugWindows` 节点 |

> 现有 `core/` 一行都不用动 —— 这正是把核心写成「不依赖 Godot」的价值。

---

## 7. 开销预算

| 项 | 频率 | 说明 |
|:---|:---|:---|
| `StepAndPresent()` 跨语言调用 | 60 Hz | 一次调用、无参数、无返回，开销在微秒级 |
| 帧缓冲跨语言传递 | **0 次** | 帧缓冲只在 C# 内部从 `Ppu` 到 `Image.SetData` |
| `GetDebugSnapshot()` | 12 Hz，且仅当有窗口可见 | 一个小 `Dictionary`，几十个标量 |
| pattern table 预览刷新 | 2 Hz | C# 内部完成，GDScript 只调 `Refresh()` |
| 窗口全关时 | 0 | `_consumers == 0` 直接 return |

---

## 8. 坑清单

1. **参数含非 Variant 类型的方法对 GDScript 不可见，而且静默失败**（实测）。
   写完之后用 `has_method("Xxx")` 自检一次，别靠猜。
2. **`private set` 的 C# 属性 GDScript 照样能写**（实测）。只读状态改用方法暴露。
3. 纯 C# 类型不可见，`EmulatorService.Console` 在 GDScript 里永远是 `null`。
4. **一个节点只能挂一个脚本** —— 不能给同一个节点同时挂 `.gd` 和 `.cs`，要拆成父子节点。
5. `embed_subwindows` 默认 `true`，独立窗口必须显式关掉（实测）。
6. 无头模式没有子窗口支持，窗口相关代码要能安全降级。
7. 窗口关闭用 `hide()` 而不是 `queue_free()`，否则每次打开都重建、位置也丢了。
8. 窗口隐藏时不要刷新内容（`DebugHub` 的 `_consumers` 计数就是干这个的）。
9. **C# 改动需要重新 build，GDScript 改动即时生效** —— 所以要把 C# 的对外面收窄，
   调 UI 的时候才不用反复重建。
10. autoload 顺序：GDScript autoload 若要访问 C# autoload，C# 那个必须排在前面
    （`project.godot` 的 `[autoload]` 按书写顺序加载）。
11. GDScript 里**不能直接用 C# 类型做类型标注**（`var c: NesConsole`），除非给 C# 类加
    `[GlobalClass]`（这一条实现时再实测确认）。稳妥起见用鸭子类型。
12. `MenuBar.prefer_global_menu` 默认 `true`，macOS 上菜单会跑到系统菜单栏，想统一就设 `false`。

---

## 9. 实施步骤与验收

| 步骤 | 内容 | 验收 |
|:---|:---|:---|
| 1 | `EmulatorService` 加快照方法 | 用探针脚本 `has_method("GetDebugSnapshot") == true`，返回的 Dictionary 键齐全 |
| 2 | 新增 `DebugHub` autoload，`EmulatorCore` 改造 | 主场景仍能跑 ROM；命令行启动日志不变 |
| 3 | 主界面换 GDScript：`MenuBar` + `Display` | 菜单可点，Display 正常出画面（用 `--dump-frame` 逐像素校验不回归） |
| 4 | CPU 窗口（独立 Window） | 窗口真独立弹出；打开时数据显示，关闭后 `_consumers` 归 0 |
| 5 | PPU 窗口 + pattern table 预览（C# 控件） | 预览图与 `--dump-frame` 导出的 pattern table 一致 |
| 6 | 删除旧 C# 调试面板 `EmulatorUi.cs` | `dotnet build` 0 警告 0 错误；UI 功能不缺失 |

自动化验收（无需人工点界面）：
写一个临时探针场景脚本，对调试窗口 `show()` 后打印其内容文本，
再用 `--headless` 跑一遍确认数据链路通、且无 GDScript 报错。

---

## 10. 实施记录

已按本文实现，`dotnet build` 0 警告 0 错误，`tests/smoke_test.tscn` 63 项断言全过
（无头和带窗口各跑一遍，退出码都是 0）。调试窗口最终做了三个：CPU、PPU、ROM 信息。

### 10.1 实施中实测到的、值得记一笔的事

1. **Godot C# 里 `Packed*Array` 就是普通 CLR 数组**，不是 `Godot.Collections.PackedXxxArray`：
   `PackedInt32Array` → `int[]`、`PackedByteArray` → `byte[]`、`PackedColorArray` → `Color[]`、
   `PackedStringArray` → `string[]`、`PackedVector2Array` → `Vector2[]`。
   所以 C# 侧快照方法直接返回 `int[]` / `Color[]`，跨到 GDScript 会自动变成对应的 Packed 数组。
   （一开始按 `Godot.Collections.PackedInt32Array` 写，编译不过。）
2. **`%唯一名` 有两条解析路径**：先看节点自己的 `owned_unique_nodes`，再看 `owner` 的。
   所以「实例化场景内部的脚本用 `%Xxx` 引用自己场景里的节点」和「主场景里的节点用 `%Xxx`
   引用同场景节点」两种写法都能工作。
3. **无头模式下 `popup_centered()` 会报 `Window 0 spawned at invalid position`**，
   因为没有真实屏幕尺寸；带窗口运行正常。冒烟测试因此把「窗口弹出」和「数据正确」
   分开断言，无头下也能测数据链路。
4. **headless 下 `DisplayServer.has_feature(SUBWINDOWS)` 是 `false`**，但 `Window` 节点依然存在、
   依然能 show/hide，只是没有真正的 OS 窗口。

### 10.2 与设计稿的偏差（都是实施时觉得更合适才改的）

| 设计稿 | 实际做法 | 原因 |
|:---|:---|:---|
| `Root → MenuBar + ScreenArea → Display` | `Root → MenuBar + Display`（Display 用 `size_flags_vertical = EXPAND_FILL`） | 多一层 `ScreenArea` 没有任何作用，VBoxContainer 直接撑开更简单 |
| 菜单项用 `set_item_shortcut` + `global = true` | 统一走 `project.godot [input]` 动作 + `_unhandled_input` | 两套机制同时存在会重复触发；而且 `Shortcut` 与 `[input]` 动作的优先级本来就是要单独验证的坑，干脆只留一套。菜单标题里直接写「(F2)」提示快捷键 |
| 窗口内容全写在 `.tscn` 里 | 骨架在 `.tscn`（CPU 窗口），重复元素（PPU 的 32 个色块）和整个 PPU 骨架用代码搭 | 32 个色块手写 tscn 不划算；寄存器网格这种固定结构还是 tscn 更好改 |
| 每个窗口一个 GDScript + 一个 tscn | 同左，但加了 `ui/debug_window.gd` 基类 | 「关闭即隐藏 + 可见才订阅」的逻辑三处复用 |

### 10.3 计划里留的待确认项，现在都清楚了

- ~~GDScript 能否用 C# 类型做类型标注~~ → **实测：把 C# autoload 标注成 `: Node` 完全没问题**，
  方法调用、属性读取（`_service.LastError`）、返回 `Dictionary` 都能正常跑，
  也没有解析错误或警告，只是编辑器里没有自动补全。
  （所以 `ui/*.gd` 里照常写了 `: Node` 标注；不用 `[GlobalClass]` 也够用。）
- ~~`Window.transient = true` 时主窗口最小化的行为~~ → 默认就是 `true`，实测窗口会跟着主窗口
  一起最小化，符合"调试窗口"的预期，不需要改。
- ~~`MenuBar` 快捷键与 `[input]` 动作冲突时的优先级~~ → 见 10.2，直接不用菜单
  `Shortcut`，避免了这个坑。

### 10.4 后续追加：ROM 信息窗口与 .nes 解析器

在本文基础上又加了第三个调试窗口（ROM 信息，F4），并把 `.nes` 解析器补完。
和窗口设计相关的两点经验：

1. **不是所有窗口都该吃 12 Hz 的快照。** ROM 信息只在装载 ROM 时变，所以 DebugHub 缓存它，
   只在监听到 C# 的 `RomLoaded` 信号（`[Signal]` 生成的 C# 事件，GDScript 直接 `connect`）后重拉一次。
   既不用每帧跨语言，窗口打开时也立刻有数据。
2. **窗口内容用代码建更划算。** ROM 信息有 17 行「标题 + 取值」，tscn 里手写 34 个 Label 不现实，
   所以 tscn 只留 `Window + Margin + VBox + GridContainer`，行在脚本里按固定表建一次，
   之后只改 `text`（不重建，避免 12 Hz 反复 new 节点）。

解析器本身的实现细节写在 `godot-nes/README.md` 的「ROM 读取」一节；
关键结论是 NES 2.0 的 mapper 高 4 位取自 **byte 8 低半字节**，
这一条在 FCEUX、fogleman/nes、jsnes 三份实现里互相印证过。

### 10.5 后续追加：深色主题与中英文国际化

设计稿里没写这两块，是后来按"样式参考浏览器深色模式、字体放大、菜单不要黑底、加中英切换"补的。
完整实现见 `godot-nes-implementation.md` 的 §12.5 / §12.6，这里只记和"界面设计"有关的取舍：

1. **样式放资源文件，不放代码。** `theme/dark_theme.tres` 是一份普通的 `Theme` 资源，
   用 `project.godot` 的 `gui/theme/custom` 挂在项目主题上 —— 项目主题是主题查找链的全局兜底，
   主窗口、每个独立 `Window`、运行中 new 出来的对话框全都吃得到，不需要任何"挂主题"的代码。
   （一开始是用 `Theme.new()` 在代码里搭，再 `apply_to()` 一个个挂 —— 那么绕是因为踩到
   "`get_tree().root.theme` **不会**传给场景里的控件"这个坑；换成项目主题之后这个坑自然消失了。
   改样式现在开 Godot 的主题编辑器改 `.tres` 就行。）
2. **字号/颜色从场景文件里搬进主题变体。** 原来 22 处 `theme_override_font_sizes` +
   `theme_override_colors` 换成 `theme_type_variation = "DimLabel"` / `"MonoLabel"`：
   改一处等于改全部，而且不会有"某个标签忘了改、字号跟别人不一样"的漏网之鱼。
3. **独立窗口要自己跟缩放。** 主窗口用 `canvas_items` 拉伸，独立 `Window` **不跟着拉伸**，
   所以主窗口一放大，调试窗口里的字就显小。`DebugWindow` 把主窗口的缩放比抄过来，
   窗口尺寸也按比例放大（按设计尺寸算，内容才不会被裁掉）。这条是"调试窗口字太小"的根因。
4. **菜单栏底色和面板同色（`#292a2d`），不是黑的。** 状态栏、菜单栏、下拉菜单、按钮
   都在同一套 surface 色阶上，只靠悬停/按下两级亮度和一条 1px 边框区分层次。
5. **CSV 翻译表的 key 就用中文原文**（不另造符号名）：代码里 `tr("打开 ROM…")` 一眼能看出
   是哪句话，漏翻时界面上显示的也是中文原文而不是 `menu.file.open` 这种占位符。
   代价是必须把 `locale/fallback` 置空，否则中文环境会被 fallback 换成英文（细节见实现文档）。
6. **切语言时只有"代码里拼出来的文字"需要手动重译**：菜单整棵重建，三个调试窗口各自覆写
   `_retranslate()`；`.tscn` 里写死的标签由基类按子类登记的表重设（引擎自己也会在排版时
   自动翻译控件文字，但那样 `.text` 属性读出来还是旧语言，无头测试就没法断言了）。
