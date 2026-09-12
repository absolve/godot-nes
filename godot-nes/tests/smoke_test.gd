extends Node

## 无头冒烟测试：把主场景装进来跑几帧，验证
## 「GDScript 主界面 → C# EmulatorCore 热路径 → 调试窗口数据链路」整条链是通的。
##
## 跑法：
##   & "<Godot 目录>\Godot_v4.7.2-stable_mono_win64_console.exe" `
##       --headless --path "<项目目录>\godot-nes" res://tests/smoke_test.tscn
##
## 退出码 0 = 全部通过，1 = 有失败项。

const ROM_PATH := "res://tests/roms/synthetic-nrom.nes"
const ROM_NROM := "res://tests/roms/synthetic-nrom.nes"
const ROM_NES20 := "res://tests/roms/synthetic-nes20.nes"
const ROM_MAPPER2 := "res://tests/roms/synthetic-mapper2.nes"
const ROM_MAPPER4 := "res://tests/roms/synthetic-mapper4.nes"
const ROM_MAPPER5 := "res://tests/roms/synthetic-mapper5.nes"
const ROM_TRUNCATED := "res://tests/roms/synthetic-truncated.nes"
const ROM_BADMAGIC := "res://tests/roms/synthetic-badmagic.nes"
const SETTLE_FRAMES := 8

var _failures := 0


func _ready() -> void:
	print("=== godot-nes 冒烟测试 ===")
	await _run()
	print("=== 结果：%s ===" % ("全部通过" if _failures == 0 else "%d 项失败" % _failures))
	get_tree().quit(0 if _failures == 0 else 1)


func _run() -> void:
	var service := get_node_or_null("/root/EmulatorService")
	var hub := get_node_or_null("/root/DebugHub")
	_check(service != null, "EmulatorService autoload 存在")
	_check(hub != null, "DebugHub autoload 存在")
	if service == null or hub == null:
		return

	# ---------------- 主界面 ----------------
	var main_scene: PackedScene = load("res://scene/main.tscn")
	_check(main_scene != null, "主场景可加载")
	if main_scene == null:
		return

	var main: Node = main_scene.instantiate()
	add_child(main)
	await _wait_frames(3)

	_check(main.get_node_or_null("Ui/Root/Display") != null, "主界面有 Display 画面节点")
	_check(main.get_node_or_null("Ui/Root/StatusBar/StatusLabel") != null, "主界面有底部状态栏")

	# 文件选择框是独立窗口：项目主题传不进去，必须显式挂，否则字小得看不清
	var file_dialog: FileDialog = main.get_node_or_null("RomFileDialog")
	_check(file_dialog != null and file_dialog.theme != null,
		"文件选择框挂上了主题（否则用 Godot 默认主题，字特别小）")
	if file_dialog != null and file_dialog.theme != null:
		_check(file_dialog.theme.default_font_size >= 19,
			"文件选择框字号跟着主题放大", str(file_dialog.theme.default_font_size))

	var menu_bar: MenuBar = main.get_node_or_null("Ui/Root/MenuBar")
	_check(menu_bar != null, "顶部有 MenuBar")
	if menu_bar != null:
		_check(menu_bar.get_menu_count() == 6,
			"菜单有 6 项（文件/模拟/视图/调试/语言/帮助）", "实际 %d" % menu_bar.get_menu_count())

	var cpu_window: Window = main.get_node_or_null("DebugWindows/CpuWindow")
	var ppu_window: Window = main.get_node_or_null("DebugWindows/PpuWindow")
	_check(cpu_window != null, "CPU 窗口节点存在")
	_check(ppu_window != null, "PPU 窗口节点存在")
	_check(cpu_window != null and not cpu_window.visible, "CPU 窗口默认隐藏（不干扰主界面）")
	_check(ppu_window != null and not ppu_window.visible, "PPU 窗口默认隐藏")

	# 这条是"独立 OS 窗口"的前提：默认值是 true（子窗口嵌在主窗口里画），必须显式关掉。
	_check(ProjectSettings.get_setting("display/window/subwindows/embed_subwindows", true) == false,
		"项目已关闭子窗口嵌入（独立 OS 窗口的前提）")

	# ---------------- ROM ----------------
	var loaded: bool = service.LoadRom(ROM_PATH)
	_check(loaded, "合成 ROM 装载成功", str(service.LastError))
	await _wait_frames(SETTLE_FRAMES)

	# ---------------- 帧是否在推进（GDScript -> C# 热路径）----------------
	var frame_a := int(service.GetSystemSnapshot()["frame"])
	await _wait_frames(10)
	var frame_b := int(service.GetSystemSnapshot()["frame"])
	_check(frame_b > frame_a, "主界面的 _process 正在驱动 C# 跑帧", "%d -> %d" % [frame_a, frame_b])

	# ---------------- 手柄输入 ----------------
	# 位序和 $4016 的移位顺序一致：bit0=A、bit1=B、bit2=Select、bit3=Start、bit4=Up…
	# 这一条是"按键到底有没有传进模拟器"的端到端检查：走的是
	# Godot Input → InputAdapter（C#）→ Bus.Controller1 这条真实路径。
	print("  -- 手柄输入 --")
	_check(int(service.GetSystemSnapshot()["controller1"]) == 0, "没按键时手柄位是 0",
		str(int(service.GetSystemSnapshot()["controller1"])))

	Input.action_press("nes_start")
	await _wait_frames(3)
	_check(int(service.GetSystemSnapshot()["controller1"]) == 0x08, "按 Start → bit3 置位",
		str(int(service.GetSystemSnapshot()["controller1"])))

	Input.action_press("nes_a")
	await _wait_frames(3)
	_check(int(service.GetSystemSnapshot()["controller1"]) == 0x09, "再按 A → bit0 也置位",
		str(int(service.GetSystemSnapshot()["controller1"])))

	Input.action_release("nes_start")
	Input.action_release("nes_a")
	await _wait_frames(3)
	_check(int(service.GetSystemSnapshot()["controller1"]) == 0, "松开之后手柄位回到 0",
		str(int(service.GetSystemSnapshot()["controller1"])))

	# 手柄 2 走的是另一套动作（键盘小键盘 / 第二个手柄设备），
	# 而且必须**不影响**手柄 1 —— 否则两人玩的时候按键会互相串。
	_check(int(service.GetSystemSnapshot()["controller2"]) == 0, "没按键时手柄 2 的位也是 0",
		str(int(service.GetSystemSnapshot()["controller2"])))

	Input.action_press("nes2_start")
	Input.action_press("nes2_right")
	await _wait_frames(3)
	_check(int(service.GetSystemSnapshot()["controller2"]) == 0x88,
		"按手柄 2 的 Start + 右 → bit3|bit7 置位",
		str(int(service.GetSystemSnapshot()["controller2"])))
	_check(int(service.GetSystemSnapshot()["controller1"]) == 0, "手柄 2 的按键不会串到手柄 1",
		str(int(service.GetSystemSnapshot()["controller1"])))

	Input.action_release("nes2_start")
	Input.action_release("nes2_right")
	await _wait_frames(3)
	_check(int(service.GetSystemSnapshot()["controller2"]) == 0, "松开之后手柄 2 回到 0",
		str(int(service.GetSystemSnapshot()["controller2"])))

	# ---------------- DebugHub 的按需订阅 ----------------
	_check(hub.consumer_count() == 0, "没有窗口打开时 DebugHub 零订阅（开销为 0）",
		"实际 %d" % hub.consumer_count())

	cpu_window.popup_centered()
	await _wait_frames(SETTLE_FRAMES)
	_check(hub.consumer_count() == 1, "打开 CPU 窗口后开始订阅", "实际 %d" % hub.consumer_count())

	var snapshot: Dictionary = hub.snapshot
	_check(not snapshot.is_empty(), "快照非空")
	if not snapshot.is_empty():
		_check(snapshot.has("cpu") and snapshot.has("ppu") and snapshot.has("system"),
			"快照含 cpu / ppu / system 三段")
		_check(bool(snapshot["system"].get("has_rom", false)), "快照报告已装载 ROM")
		_check(int(snapshot["system"].get("mapper", -1)) == 0, "快照报告 mapper 0")

	# ---------------- CPU 窗口内容 ----------------
	var pc_label: Label = cpu_window.get_node_or_null("%PcValue")
	_check(pc_label != null, "CPU 窗口里能取到 %PcValue")
	if pc_label != null:
		_check(pc_label.text != "----", "CPU 窗口显示出 PC 值", "实际 '%s'" % pc_label.text)

	var flags_label: RichTextLabel = cpu_window.get_node_or_null("%FlagsLabel")
	_check(flags_label != null and flags_label.text.length() > 0, "CPU 窗口显示出标志位")

	var stack_label: Label = cpu_window.get_node_or_null("%StackLabel")
	_check(stack_label != null and stack_label.text.split(" ").size() == 8,
		"CPU 窗口显示出 8 字节栈顶", "实际 '%s'" % (stack_label.text if stack_label else "?"))

	# ---------------- PPU 窗口内容 ----------------
	ppu_window.popup_centered()
	await _wait_frames(SETTLE_FRAMES)
	_check(hub.consumer_count() == 2, "两个窗口都打开时订阅数为 2", "实际 %d" % hub.consumer_count())

	var ctrl_label: Label = ppu_window.get_node_or_null("%CtrlValue")
	_check(ctrl_label != null and ctrl_label.text != "--", "PPU 窗口显示出 CTRL",
		"实际 '%s'" % (ctrl_label.text if ctrl_label else "?"))

	var vram_label: Label = ppu_window.get_node_or_null("%VramValue")
	_check(vram_label != null and vram_label.text.length() == 4, "PPU 窗口显示出 VRAM 地址",
		"实际 '%s'" % (vram_label.text if vram_label else "?"))

	var palette_grid: GridContainer = ppu_window.get_node_or_null("%PaletteGrid")
	_check(palette_grid != null and palette_grid.get_child_count() == 32,
		"PPU 窗口画出 32 个调色板色块",
		"实际 %d" % (palette_grid.get_child_count() if palette_grid else -1))

	var table0: TextureRect = ppu_window.get_node_or_null("%Table0")
	_check(table0 != null and table0.texture != null, "PPU 窗口的 pattern table 预览有纹理")

	# ---------------- ROM 信息窗口 ----------------
	var rom_window: Window = main.get_node_or_null("DebugWindows/RomWindow")
	_check(rom_window != null, "ROM 信息窗口节点存在")
	if rom_window != null:
		_check(not rom_window.visible, "ROM 信息窗口默认隐藏")
		rom_window.popup_centered()
		await _wait_frames(SETTLE_FRAMES)
		_check(hub.consumer_count() == 3, "三个窗口都打开时订阅数为 3",
			"实际 %d" % hub.consumer_count())

		var grid: GridContainer = rom_window.get_node_or_null("%InfoGrid")
		_check(grid != null and grid.get_child_count() == 34, "ROM 窗口建出 17 行「标题 + 取值」",
			"实际 %d" % (grid.get_child_count() if grid else -1))

		var found_mapper := false
		var found_checksum := false
		if grid != null:
			for child in grid.get_children():
				if child is Label:
					if "NROM" in child.text:
						found_mapper = true
					if str(service.GetRomInfo().get("md5", "")) in child.text:
						found_checksum = true
		_check(found_mapper, "ROM 窗口里显示出了 mapper 名字（NROM）")
		_check(found_checksum, "ROM 窗口里显示出了 MD5 校验和")

	# ---------------- 关闭窗口后回到零开销 ----------------
	cpu_window.hide()
	ppu_window.hide()
	if rom_window != null:
		rom_window.hide()
	await _wait_frames(3)
	_check(hub.consumer_count() == 0, "关掉窗口后 DebugHub 回到零订阅",
		"实际 %d" % hub.consumer_count())

	# ---------------- ROM 解析：iNES 1.0 / NES 2.0 / 错误路径 ----------------
	print("  -- ROM 解析 --")

	_check(service.LoadRom(ROM_NROM), "iNES 1.0 NROM 解析成功", str(service.LastError))
	var nrom: Dictionary = service.GetRomInfo()
	_check(str(nrom.get("format", "")) == "iNES 1.0", "识别为 iNES 1.0", str(nrom.get("format", "")))
	_check(int(nrom.get("mapper", -1)) == 0, "mapper = 0")
	_check(str(nrom.get("mapper_name", "")) == "NROM", "mapper 名字 = NROM",
		str(nrom.get("mapper_name", "")))
	_check(int(nrom.get("prg_banks", 0)) == 1, "程序块 = 1 x 16KB", str(nrom.get("prg_banks", 0)))
	_check(int(nrom.get("chr_banks", 0)) == 1, "图案块 = 1 x 8KB", str(nrom.get("chr_banks", 0)))
	_check(int(nrom.get("reset_vector", 0)) == 0x8000, "从程序块尾部读出复位向量 $8000",
		"$%04X" % int(nrom.get("reset_vector", 0)))
	_check(str(nrom.get("crc32", "")).length() == 8, "算了 CRC32", str(nrom.get("crc32", "")))
	_check(str(nrom.get("md5", "")).length() == 32, "算了 MD5", str(nrom.get("md5", "")))
	_check(not bool(nrom.get("battery", true)), "无电池标志")

	_check(service.LoadRom(ROM_NES20), "NES 2.0 解析成功", str(service.LastError))
	var nes20: Dictionary = service.GetRomInfo()
	_check(str(nes20.get("format", "")) == "NES 2.0", "识别为 NES 2.0", str(nes20.get("format", "")))
	_check(int(nes20.get("submapper", -1)) == 1, "子 mapper = 1（byte 8 高半字节）",
		str(nes20.get("submapper", -1)))
	_check(int(nes20.get("prg_banks", 0)) == 2, "程序块 = 2 x 16KB", str(nes20.get("prg_banks", 0)))
	_check(bool(nes20.get("uses_chr_ram", false)), "没有 CHR ROM，走 CHR RAM")
	_check(int(nes20.get("chr_ram_kb", 0)) == 8, "CHR RAM = 8KB（byte 11 低半字节）",
		str(nes20.get("chr_ram_kb", 0)))
	_check(int(nes20.get("wram_kb", 0)) == 8, "WRAM = 8KB（byte 10 低半字节）",
		str(nes20.get("wram_kb", 0)))
	_check(int(nes20.get("nvram_kb", 0)) == 4, "NVRAM = 4KB（byte 10 高半字节）",
		str(nes20.get("nvram_kb", 0)))
	_check(str(nes20.get("tv_system", "")) == "PAL", "制式 = PAL（byte 12）",
		str(nes20.get("tv_system", "")))
	_check(bool(nes20.get("battery", false)), "有电池标志")

	# ---------------- 映射器支持列表 ----------------
	print("  -- 映射器 --")

	# 每一块都要求：能装载、mapper 号对、家族名对、而且报告"已实现"
	var mapper_cases := [
		[ROM_MAPPER2, 2, "UxROM", "mapper2"],
		[ROM_MAPPER4, 4, "MMC3 (TxROM)", "mapper4"],
	]
	for case in mapper_cases:
		var path: String = case[0]
		var number: int = case[1]
		var family: String = case[2]
		var label: String = case[3]
		_check(service.LoadRom(path), "%s 能装载" % label, str(service.LastError))
		var info: Dictionary = service.GetRomInfo()
		_check(int(info.get("mapper", -1)) == number, "%s 的 mapper 号 = %d" % [label, number],
			str(info.get("mapper", -1)))
		_check(str(info.get("mapper_name", "")) == family, "%s 的家族名 = %s" % [label, family],
			str(info.get("mapper_name", "")))
		_check(bool(info.get("mapper_implemented", false)), "%s 报告「已实现」" % label)

	# 已实现的 mapper 号：0 / 1 / 2 / 3 / 4 / 7（UI 里也是靠这个显示"已实现/计划中"）
	var implemented: Array = service.GetImplementedMappers()
	for number in [0, 1, 2, 3, 4, 7]:
		_check(number in implemented, "mapper %d 在已实现列表里" % number, str(implemented))
	_check(not (5 in implemented), "mapper 5 还不在已实现列表里", str(implemented))

	# PPU 快照里的镜像/映射器名字要跟着卡带走
	service.LoadRom(ROM_MAPPER4)
	var ppu_snapshot: Dictionary = service.GetPpuSnapshot()
	_check(str(ppu_snapshot.get("mapper", "")) == "MMC3 (TxROM)", "PPU 快照报告当前 mapper",
		str(ppu_snapshot.get("mapper", "")))
	_check(str(ppu_snapshot.get("mirroring", "")) != "", "PPU 快照给出当前生效的镜像",
		str(ppu_snapshot.get("mirroring", "")))
	_check(not bool(ppu_snapshot.get("mapper_irq", true)), "MMC3 刚装载时没有挂起的 IRQ")

	# 错误路径：不能崩，而且要给得出看得懂的原因
	service.LoadRom(ROM_NROM)          # 先装一个 mapper 0，下面验证失败装载不会把它搞坏
	_check(not service.LoadRom(ROM_MAPPER5), "未实现的 mapper 会被拒绝")
	_check("Mapper 5" in str(service.LastError), "错误信息说明了是哪个 mapper", str(service.LastError))
	_check(not service.LoadRom(ROM_TRUNCATED), "被截断的 ROM 会被拒绝")
	_check("截断" in str(service.LastError), "错误信息说明了是截断", str(service.LastError))
	_check(not service.LoadRom(ROM_BADMAGIC), "魔数不对的文件会被拒绝")
	_check("魔数" in str(service.LastError), "错误信息说明了是魔数不对", str(service.LastError))

	# 解析失败不应该把已经装载的卡带搞坏
	_check(int(service.GetRomInfo().get("mapper", -1)) == 0, "失败的装载不影响已装载的卡带")

	# ---------------- 关闭 ROM ----------------
	print("  -- 关闭 ROM --")

	_check(service.LoadRom(ROM_NROM), "先装一个 ROM 准备关掉", str(service.LastError))
	_check(bool(service.HasRom), "HasRom 报告已装载")

	service.CloseRom()
	_check(not bool(service.HasRom), "CloseRom 之后 HasRom 变成 false")
	_check(not bool(service.GetRomInfo().get("has_rom", true)), "ROM 信息报告「未装载」")
	_check(str(service.CurrentRomPath) == "", "ROM 路径被清空", str(service.CurrentRomPath))
	_check(str(service.CurrentRomDescription) == "", "ROM 描述被清空")

	# 没有卡带就不该再推进帧（真机此时执行的是总线垃圾，没有模拟意义）
	var framesBefore := int(service.GetSystemSnapshot()["frame"])
	await _wait_frames(10)
	var framesAfter := int(service.GetSystemSnapshot()["frame"])
	_check(framesAfter == framesBefore, "没有卡带时不再推进帧",
		"%d -> %d" % [framesBefore, framesAfter])

	# 关闭 ROM 后必须清屏：上一帧不能留在屏幕上，否则看着像还装着 ROM
	var core_node: Node = main.get_node_or_null("EmulatorCore")
	_check(core_node != null and int(core_node.DisplayClearCount) >= 1,
		"关闭 ROM 时主动清屏了（否则上一帧会留在屏幕上）",
		str(core_node.DisplayClearCount) if core_node != null else "找不到 EmulatorCore")

	# 关掉之后还能再装回来
	_check(service.LoadRom(ROM_NES20), "关掉之后可以重新装载", str(service.LastError))

	_run_theme(main, menu_bar, cpu_window)
	await _run_i18n(main, menu_bar)


## 主题：确认 theme/dark_theme.tres 真的生效了
## （无头测不了"好不好看"，能测的是字号、变体、配色这些具体数值）。
func _run_theme(main: Node, menu_bar: MenuBar, cpu_window: Window) -> void:
	print("  -- 主题 --")

	var ui_settings := get_node_or_null("/root/UiSettings")
	_check(ui_settings != null, "UiSettings autoload 存在")

	# 样式必须是"资源文件 + 项目主题"，不是代码建出来的
	_check(str(ProjectSettings.get_setting("gui/theme/custom", "")) == "res://theme/dark_theme.tres",
		"项目主题指向 theme/dark_theme.tres",
		str(ProjectSettings.get_setting("gui/theme/custom", "")))
	var project_theme: Theme = ThemeDB.get_project_theme()
	_check(project_theme != null, "项目主题已加载")
	_check(ResourceLoader.exists("res://theme/dark_theme.tres"), "主题文件存在")
	if project_theme == null:
		return

	_check(project_theme.default_font_size >= 19, "基础字号比 Godot 默认的 16 大不少",
		"实际 %d" % project_theme.default_font_size)

	# 画面四周的留白也是深灰蓝，不是纯黑（这样主窗口和独立调试窗口的底色一致）
	var clear := RenderingServer.get_default_clear_color()
	_check(not clear.is_equal_approx(Color(0, 0, 0, 1)), "清屏色不是纯黑", str(clear))
	_check(clear.is_equal_approx(Color("202124")), "清屏色 = 主题底色 #202124", str(clear))

	if menu_bar != null:
		var menu_normal := menu_bar.get_theme_stylebox("normal")
		_check(menu_normal is StyleBoxFlat, "菜单栏用的是 StyleBoxFlat")
		if menu_normal is StyleBoxFlat:
			var bg: Color = (menu_normal as StyleBoxFlat).bg_color
			_check(not bg.is_equal_approx(Color(0, 0, 0, 1)), "菜单栏底色不是黑色",
				str(bg))
			_check(bg.is_equal_approx(Color("292a2d")), "菜单栏底色 = 面板色 #292a2d", str(bg))

	# 独立调试窗口不会跟着主窗口的 canvas_items 拉伸走，得自己跟；
	# 不然主窗口一放大，这里的字看上去就小一圈（这是这次的报障）。
	if cpu_window != null:
		var design_height: int = int(ProjectSettings.get_setting("display/window/size/viewport_height", 768))
		var root := get_tree().root
		var original_size := root.size

		if DisplayServer.get_name() == "headless":
			# 无头下没有真实窗口，改尺寸也不会触发拉伸，留到"带窗口"那一遍真跑
			_check(is_equal_approx(cpu_window.content_scale_factor, 1.0),
				"调试窗口的缩放系数（无头下主窗口就是设计尺寸，所以是 1）")
			_check(true, "主窗口放大后调试窗口跟着放大（只在带窗口时真跑）")
		else:
			root.size = Vector2i(design_height * 2, design_height * 2)   # 放大到 2 倍
			await _wait_frames(3)
			_check(is_equal_approx(cpu_window.content_scale_factor, 2.0),
				"主窗口放大 2 倍后调试窗口也跟着放大 2 倍",
				str(cpu_window.content_scale_factor))
			_check(cpu_window.size.y >= 340 * 2 - 4,
				"窗口尺寸也一起放大（不然内容会被裁掉）", str(cpu_window.size))

			root.size = original_size
			await _wait_frames(3)
			_check(is_equal_approx(cpu_window.content_scale_factor, 1.0),
				"缩回去之后调试窗口也缩回来", str(cpu_window.content_scale_factor))

	if cpu_window != null:
		# 两个类型变体要能真的穿过主题链查到东西，光看 .tres 里注册了不算数
		var value_label: Label = cpu_window.get_node_or_null("%PcValue")
		_check(value_label != null and value_label.get_theme_font_size("font_size") >= 19,
			"调试窗口里的字号也跟着放大了",
			str(value_label.get_theme_font_size("font_size")) if value_label != null else "找不到节点")
		_check(value_label != null and value_label.get_theme_font("font") is SystemFont,
			"MonoLabel 的值用的是等宽字体")

		var caption: Label = cpu_window.get_node_or_null("%FlagsCaption")
		_check(caption != null and caption.theme_type_variation == "DimLabel",
			"调试窗口的表头用 DimLabel 变体，而不是写死字号")
		if caption != null:
			_check(caption.get_theme_font_size("font_size") == 18,
				"DimLabel 字号 = 18（比正文小一档）",
				str(caption.get_theme_font_size("font_size")))
			_check(caption.get_theme_color("font_color").is_equal_approx(Color("9aa0a6")),
				"DimLabel 的文字更暗", str(caption.get_theme_color("font_color")))

	var status_label: Label = main.get_node_or_null("Ui/Root/StatusBar/StatusLabel")
	_check(status_label != null and status_label.get_theme_font_size("font_size") >= 18,
		"状态栏字号也放大了（它走 DimLabel，原来只有 12）",
		str(status_label.get_theme_font_size("font_size")) if status_label != null else "找不到节点")
	if status_label != null:
		_check(status_label.text.contains("\n"), "状态栏分两行写（文件名一行、运行状态一行）",
			status_label.text.replace("\n", " ⏎ "))
		_check(status_label.autowrap_mode != TextServer.AUTOWRAP_OFF,
			"状态栏开了自动换行，长文件名不会顶出屏幕")
		# 换行要真的按宽度算：给一段超长文字，行数必须比两行多
		var before := status_label.text
		status_label.text = "很长的名字 ".repeat(60)
		var wrapped_lines := status_label.get_line_count()
		status_label.text = before
		_check(wrapped_lines > 2, "超长文件名会折成多行（数到 %d 行）" % wrapped_lines)


## 国际化：真刀真枪切一次语言，检查菜单、窗口标题、tscn 静态表头、格式化字符串都跟着变。
func _run_i18n(main: Node, menu_bar: MenuBar) -> void:
	print("  -- 国际化（中英切换）--")

	var ui_settings := get_node_or_null("/root/UiSettings")
	_check(ui_settings != null, "UiSettings autoload 存在")
	if ui_settings == null or menu_bar == null:
		return

	var rom_window: Window = main.get_node_or_null("DebugWindows/RomWindow")
	var cpu_window: Window = main.get_node_or_null("DebugWindows/CpuWindow")
	var ppu_window: Window = main.get_node_or_null("DebugWindows/PpuWindow")
	var original: String = ui_settings.language

	# 先把窗口都打开：静态表头是 _retranslate 里翻的，取值要等可见后拿到新快照
	if cpu_window != null:
		cpu_window.popup_centered()
	if ppu_window != null:
		ppu_window.popup_centered()
	if rom_window != null:
		rom_window.popup_centered()
	await _wait_frames(SETTLE_FRAMES)

	_check(TranslationServer.get_loaded_locales().has("en"),
		"语言表已导入（en）", str(TranslationServer.get_loaded_locales()))

	# ---- 英文 ----
	ui_settings.set_language("en")
	await _wait_frames(3)

	_check(ui_settings.language == "en", "切到英文")
	_check(TranslationServer.get_locale() == "en", "TranslationServer 的 locale 也切了")
	_check(menu_bar.get_menu_count() == 6, "切语言后菜单还是 6 项",
		"实际 %d" % menu_bar.get_menu_count())
	_check(menu_bar.get_menu_title(0) == "File", "菜单 0 = File", menu_bar.get_menu_title(0))
	_check(menu_bar.get_menu_title(3) == "Debug", "菜单 3 = Debug", menu_bar.get_menu_title(3))
	_check(menu_bar.get_menu_title(4) == "Language", "菜单 4 = Language", menu_bar.get_menu_title(4))
	if rom_window != null:
		_check(rom_window.title == "ROM Info", "ROM 窗口标题 = ROM Info", rom_window.title)
	if cpu_window != null:
		var flags_caption: Label = cpu_window.get_node_or_null("Margin/VBox/FlagsCaption")
		_check(flags_caption != null and flags_caption.text == "Flags P (lit = set)",
			"tscn 里的静态表头也被翻了", flags_caption.text if flags_caption != null else "找不到节点")

	# 格式化字符串：% 占位符必须原样保留，不然 tr() 出来就废了
	_check(tr("名称表镜像：%s") == "Mirroring: %s", "带占位符的翻译", tr("名称表镜像：%s"))
	_check(tr("水平") == "Horizontal", "镜像值也要能翻（C# 侧给的是中文原文）", tr("水平"))
	_check(tr("小键盘 0") == "Numpad 0", "手柄 2 的按键说明也有英文", tr("小键盘 0"))
	_check(tr("NES 帧/秒") == "NES fps", "状态栏的帧率标签也有英文", tr("NES 帧/秒"))
	var service_node := get_node_or_null("/root/EmulatorService")
	_check(service_node != null and service_node.GetSystemSnapshot().has("nes_fps"),
		"系统快照给出模拟器自己的 NES 帧率")

	# PPU 窗口那一行"名称表镜像：xxx"里的 xxx 是 C# 快照给的中文，得跟着语言走
	if ppu_window != null:
		var ppu_flags: RichTextLabel = ppu_window.get_node_or_null("%FlagsLabel")
		if ppu_flags != null:
			_check("Mirroring:" in ppu_flags.text and not ("水平" in ppu_flags.text
				or "垂直" in ppu_flags.text),
				"PPU 窗口的镜像也跟着翻了", ppu_flags.text)
	_check(tr("已装载：") == "Loaded: ", "值里有结尾空格（拼文件名要留）",
		"[%s]" % tr("已装载："))
	_check(tr("文件") == "File", "同一个 key 两种用法都能查", tr("文件"))

	# ROM 窗口的行标题是代码建的，得自己重译
	if rom_window != null:
		var grid: GridContainer = rom_window.get_node_or_null("Margin/VBox/InfoGrid")
		if grid != null and grid.get_child_count() >= 4:
			_check((grid.get_child(0) as Label).text == "File", "ROM 行标题 0 = File",
				(grid.get_child(0) as Label).text)
			_check((grid.get_child(4) as Label).text == "Format", "ROM 行标题 2 = Format",
				(grid.get_child(4) as Label).text)
			# 取值也要跟着重画（切换语言时用缓存的快照原地刷新）
			_check((grid.get_child(5) as Label).text == "NES 2.0", "ROM 行的值没有丢",
				(grid.get_child(5) as Label).text)

	# ---- 中文 ----
	ui_settings.set_language("zh")
	await _wait_frames(3)

	_check(ui_settings.language == "zh", "切回中文")
	_check(menu_bar.get_menu_title(0) == "文件", "菜单 0 = 文件", menu_bar.get_menu_title(0))
	_check(tr("文件") == "文件", "中文时 tr() 原样返回 key", tr("文件"))
	if rom_window != null:
		_check(rom_window.title == "ROM 信息", "ROM 窗口标题 = ROM 信息", rom_window.title)
		var grid: GridContainer = rom_window.get_node_or_null("Margin/VBox/InfoGrid")
		if grid != null and grid.get_child_count() >= 2:
			_check((grid.get_child(0) as Label).text == "文件", "切回中文后行标题也回来了",
				(grid.get_child(0) as Label).text)

	ui_settings.set_language(original)
	await _wait_frames(2)

	if cpu_window != null:
		cpu_window.hide()
	if rom_window != null:
		rom_window.hide()
	await _wait_frames(2)


func _check(condition: bool, description: String, detail: String = "") -> void:
	if condition:
		print("  [OK]   ", description)
	else:
		_failures += 1
		print("  [FAIL] ", description, ("   " + detail) if detail != "" else "")


func _wait_frames(count: int) -> void:
	for _i in count:
		await get_tree().process_frame
