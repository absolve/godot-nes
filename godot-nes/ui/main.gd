extends Node

## 主界面（GDScript）：顶部菜单 + 游戏画面 + 底部状态栏。
##
## 模拟器本身跑在 C# 的 EmulatorCore 里，这里每帧只调一次 StepAndPresent()，
## 帧缓冲（245 KB）永远不会跨语言传递。
## CPU / PPU 的详细状态放在独立 Window 里（F2 / F3），主窗口保持干净。

enum MenuId {
	OPEN_ROM = 1,
	CLOSE_ROM,
	QUIT,
	RESET,
	TOGGLE_PAUSE,
	STEP_INSTRUCTION,
	WIN_CPU,
	WIN_PPU,
	WIN_ROM,
	FULLSCREEN,
	TOGGLE_STATUSBAR,
	SNAPSHOT_PNG,
	SHORTCUTS,
	ABOUT,
	LANG_ZH,
	LANG_EN,
}

const STATUS_INTERVAL := 0.25

## 深色主题资源路径（给独立窗口手动挂主题用）。
const THEME_PATH := "res://theme/dark_theme.tres"

## 独立窗口（文件框 / 提示框）的整体缩放：主题字号之外再放大一档。
const DIALOG_SCALE := 1.35
const MENU_VIEW := "视图"
const MENU_DEBUG := "调试"
const MENU_LANGUAGE := "语言"

@onready var _menu_bar: MenuBar = $Ui/Root/MenuBar
@onready var _status_bar: PanelContainer = $Ui/Root/StatusBar
@onready var _status_label: Label = $Ui/Root/StatusBar/StatusLabel
@onready var _file_dialog: FileDialog = $RomFileDialog
@onready var _cpu_window: Window = $DebugWindows/CpuWindow
@onready var _ppu_window: Window = $DebugWindows/PpuWindow
@onready var _rom_window: Window = $DebugWindows/RomWindow

## 这两个是 C# 脚本 / C# autoload：GDScript 的静态分析看不到它们的成员（没有自动补全），
## 但运行时调用完全正常（方法、属性、Dictionary 返回值都实测过），所以照常标注成 Node。
@onready var _core: Node = $EmulatorCore
var _service: Node = null

var _menus: Dictionary = {}
var _status_timer := 0.0
var _flash_message := ""
var _flash_timer := 0.0
var _last_frame := 0


func _ready() -> void:
	# 窗口底不是纯黑：画面四周的留白用主题里的底色（浏览器深色模式那种深灰蓝）。
	# 这个默认清屏色是全局的，主窗口、独立调试窗口、对话框都吃它。
	# 颜色值本身在 theme/dark_theme.tres 里（自定义条目 NesTheme/window_background）。
	RenderingServer.set_default_clear_color(UiSettings.window_background())

	_service = get_node_or_null("/root/EmulatorService")
	if _service == null:
		push_error("[godot-nes] 主界面找不到 EmulatorService 自动加载。")

	_build_menus()
	_configure_file_dialog()
	_watch_windows()
	_update_window_menu_state()
	_update_rom_menu_state()
	_update_status()

	# 样式由 project.godot 的 gui/theme/custom 全局提供（theme/dark_theme.tres），
	# 这里只需要管语言：切语言时把代码里拼出来的文字重译一遍。
	UiSettings.locale_changed.connect(_on_locale_changed)


func _physics_process(delta: float) -> void:
	# 用固定步长（默认 60 步/秒）驱动模拟器：步长是常量，模拟节奏不会被显示刷新率带跑
	# （165Hz 屏上 _process 每步只有 6ms，节奏会跟着显示器抖）。
	# 内部完成「采样输入 + 按时间累积跑够 1 帧 + 上传纹理」，只跨一次语言边界、不传参数。
	_core.StepAndPresent()

	_flash_timer = maxf(0.0, _flash_timer - delta)

	_status_timer += delta
	if _status_timer >= STATUS_INTERVAL:
		_status_timer = 0.0
		_update_status()


func _unhandled_input(event: InputEvent) -> void:
	# Q：把当前这一帧的完整快照存下来（排查画面问题时用，不属于 NES 按键）
	if event is InputEventKey and event.pressed and not event.echo and event.keycode == KEY_Q:
		if _core != null:
			_core.MarkPlayPosition()
			_flash(tr("已记录当前帧的快照"))

		get_viewport().set_input_as_handled()
		return

	if event.is_action_pressed("emulator_load_rom"):
		_open_rom_dialog()
	elif event.is_action_pressed("emulator_reset"):
		if _service != null:
			_service.Reset()
	elif event.is_action_pressed("emulator_pause"):
		_toggle_pause()
	elif event.is_action_pressed("emulator_step_frame"):
		if _service != null:
			_service.StepInstruction()
	elif event.is_action_pressed("emulator_toggle_debug"):
		_toggle_status_bar()
	elif event.is_action_pressed("emulator_window_cpu"):
		_toggle_window(_cpu_window)
	elif event.is_action_pressed("emulator_window_ppu"):
		_toggle_window(_ppu_window)
	elif event.is_action_pressed("emulator_window_rom"):
		_toggle_window(_rom_window)
	elif event.is_action_pressed("emulator_fullscreen"):
		_toggle_fullscreen()
	elif event.is_action_pressed("emulator_quit"):
		get_tree().quit()
	else:
		return

	get_viewport().set_input_as_handled()


# ---------------------------------------------------------------------------
# 菜单
# ---------------------------------------------------------------------------

func _build_menus() -> void:
	# 默认是 true：macOS 上菜单会跑到系统菜单栏，两端外观差很多，这里统一成窗口内菜单。
	_menu_bar.prefer_global_menu = false

	var file_menu := _add_menu("文件")
	file_menu.add_item(tr("打开 ROM…"), MenuId.OPEN_ROM)
	file_menu.add_item(tr("关闭 ROM"), MenuId.CLOSE_ROM)
	file_menu.add_separator()
	file_menu.add_item(tr("退出"), MenuId.QUIT)

	var emulation_menu := _add_menu("模拟")
	emulation_menu.add_item(tr("复位"), MenuId.RESET)
	emulation_menu.add_item(tr("暂停 / 继续"), MenuId.TOGGLE_PAUSE)
	emulation_menu.add_item(tr("单步一条指令"), MenuId.STEP_INSTRUCTION)

	var view_menu := _add_menu(MENU_VIEW)
	view_menu.add_check_item(tr("CPU 窗口 (F2)"), MenuId.WIN_CPU)
	view_menu.add_check_item(tr("PPU 窗口 (F3)"), MenuId.WIN_PPU)
	view_menu.add_check_item(tr("ROM 信息窗口 (F4)"), MenuId.WIN_ROM)
	view_menu.add_separator()
	view_menu.add_item(tr("全屏 (F11)"), MenuId.FULLSCREEN)

	var debug_menu := _add_menu(MENU_DEBUG)
	debug_menu.add_check_item(tr("显示状态栏 (F1)"), MenuId.TOGGLE_STATUSBAR)
	debug_menu.set_item_checked(debug_menu.get_item_index(MenuId.TOGGLE_STATUSBAR), true)
	debug_menu.add_separator()
	debug_menu.add_item(tr("当前帧存成 PNG"), MenuId.SNAPSHOT_PNG)

	# 语言：两个单选项，勾当前那个
	var language_menu := _add_menu(MENU_LANGUAGE)
	language_menu.add_radio_check_item("中文", MenuId.LANG_ZH)
	language_menu.add_radio_check_item("English", MenuId.LANG_EN)
	_update_language_menu_state()

	var help_menu := _add_menu("帮助")
	help_menu.add_item(tr("快捷键"), MenuId.SHORTCUTS)
	help_menu.add_item(tr("关于"), MenuId.ABOUT)

	for menu in _menus.values():
		menu.id_pressed.connect(_on_menu_pressed)


func _add_menu(title: String) -> PopupMenu:
	var menu := PopupMenu.new()
	# 菜单名只用于内部节点命名，标题另设
	menu.name = "Menu%d" % _menus.size()
	_menu_bar.add_child(menu)

	# 菜单标题就是子节点顺序，必须和 add_child 的顺序一致。
	_menu_bar.set_menu_title(_menus.size(), tr(title))
	_menus[title] = menu
	return menu


## 语言菜单的勾选状态跟着当前语言走。
func _update_language_menu_state() -> void:
	var language_menu := _menus.get(MENU_LANGUAGE) as PopupMenu
	if language_menu == null or UiSettings == null:
		return

	language_menu.set_item_checked(
		language_menu.get_item_index(MenuId.LANG_ZH), UiSettings.is_language("zh"))
	language_menu.set_item_checked(
		language_menu.get_item_index(MenuId.LANG_EN), UiSettings.is_language("en"))


func _on_locale_changed(_code: String) -> void:
	_retranslate()


## 语言切换后重建菜单、更新窗口标题和状态栏。
## tscn 里的静态文字 Godot 会自动重译，但这些是代码里写的，得自己来。
func _retranslate() -> void:
	# 菜单整个重建：文字很多，逐个 set_item_text 更容易漏。
	# 注意必须 remove_child + free 而不是 queue_free —— MenuBar 内部有一份"菜单表"，
	# 是在 add_child/remove_child 时同步维护的；queue_free 是延迟的，那一帧里旧菜单
	# 还挂在表上，set_menu_title() 会设到旧节点上，标题就会变成 @PopupMenu@123 这种。
	for child in _menu_bar.get_children():
		_menu_bar.remove_child(child)
		child.free()
	_menus.clear()
	_build_menus()

	# 各调试窗口的标题由它们自己重译（rom_window 会做；CPU/PPU 两种语言写法相同）

	_update_rom_menu_state()
	_update_window_menu_state()
	_update_status()


func _on_menu_pressed(id: int) -> void:
	match id:
		MenuId.OPEN_ROM:
			_open_rom_dialog()
		MenuId.CLOSE_ROM:
			_close_rom()
		MenuId.QUIT:
			get_tree().quit()
		MenuId.RESET:
			if _service != null:
				_service.Reset()
			_flash(tr("复位"))
		MenuId.TOGGLE_PAUSE:
			_toggle_pause()
		MenuId.STEP_INSTRUCTION:
			if _service != null:
				_service.StepInstruction()
			_update_status()
		MenuId.WIN_CPU:
			_toggle_window(_cpu_window)
		MenuId.WIN_PPU:
			_toggle_window(_ppu_window)
		MenuId.WIN_ROM:
			_toggle_window(_rom_window)
		MenuId.FULLSCREEN:
			_toggle_fullscreen()
		MenuId.TOGGLE_STATUSBAR:
			_toggle_status_bar()
		MenuId.SNAPSHOT_PNG:
			_take_snapshot()
		MenuId.SHORTCUTS:
			_show_shortcuts()
		MenuId.ABOUT:
			_show_about()
		MenuId.LANG_ZH:
			UiSettings.set_language("zh")
		MenuId.LANG_EN:
			UiSettings.set_language("en")


# ---------------------------------------------------------------------------
# 调试窗口
# ---------------------------------------------------------------------------

## 没装 ROM 时把「关闭 ROM」置灰，免得点了没反应像坏了。
func _update_rom_menu_state() -> void:
	var file_menu := _menus.get("文件") as PopupMenu
	if file_menu == null or _service == null:
		return

	var has_rom: bool = _service.HasRom
	file_menu.set_item_disabled(file_menu.get_item_index(MenuId.CLOSE_ROM), not has_rom)


func _watch_windows() -> void:
	# 窗口也可能被自己的关闭按钮关掉，所以订阅可见性变化来同步菜单勾选状态。
	if _cpu_window != null:
		_cpu_window.visibility_changed.connect(_update_window_menu_state)
	if _ppu_window != null:
		_ppu_window.visibility_changed.connect(_update_window_menu_state)
	if _rom_window != null:
		_rom_window.visibility_changed.connect(_update_window_menu_state)


func _toggle_window(window: Window) -> void:
	if window == null:
		return

	if window.visible:
		window.hide()
	else:
		window.popup_centered()

	_update_window_menu_state()


func _update_window_menu_state() -> void:
	var view_menu := _menus.get(MENU_VIEW) as PopupMenu
	if view_menu == null:
		return

	var cpu_visible := _cpu_window != null and _cpu_window.visible
	var ppu_visible := _ppu_window != null and _ppu_window.visible
	var rom_visible := _rom_window != null and _rom_window.visible
	view_menu.set_item_checked(view_menu.get_item_index(MenuId.WIN_CPU), cpu_visible)
	view_menu.set_item_checked(view_menu.get_item_index(MenuId.WIN_PPU), ppu_visible)
	view_menu.set_item_checked(view_menu.get_item_index(MenuId.WIN_ROM), rom_visible)


func _toggle_status_bar() -> void:
	if _status_bar == null:
		return

	_status_bar.visible = not _status_bar.visible

	var debug_menu := _menus.get(MENU_DEBUG) as PopupMenu
	if debug_menu != null:
		debug_menu.set_item_checked(
			debug_menu.get_item_index(MenuId.TOGGLE_STATUSBAR), _status_bar.visible)


func _toggle_fullscreen() -> void:
	if DisplayServer.window_get_mode() == DisplayServer.WINDOW_MODE_FULLSCREEN:
		DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_WINDOWED)
	else:
		DisplayServer.window_set_mode(DisplayServer.WINDOW_MODE_FULLSCREEN)


# ---------------------------------------------------------------------------
# ROM 装载
# ---------------------------------------------------------------------------

## FileDialog 和运行时的 AcceptDialog 都是**独立窗口**：项目的全局主题（gui/theme/custom）
## 只作用于主窗口内的控件，传不到这些 Window 上（和调试窗口踩过的坑一样）。
## 不显式挂主题的话，它们用 Godot 默认主题，字号和列表项都小得看不清。
func _apply_dark_theme(window: Window) -> void:
	if window == null:
		return

	if ResourceLoader.exists(THEME_PATH):
		window.theme = load(THEME_PATH)

	# 主题只管字体和配色，字号仍偏小；这些独立窗口再整体放大一档，
	# 文件列表、按钮才是真正看得清的大小。
	window.content_scale_factor = DIALOG_SCALE

func _configure_file_dialog() -> void:
	if _file_dialog == null:
		return

	# FileDialog 是**独立窗口**，项目的全局主题传不到它身上（和调试窗口一个道理），
	# 不显式挂的话它用的是 Godot 默认主题 —— 字和列表都非常小。
	_apply_dark_theme(_file_dialog)

	_file_dialog.file_mode = FileDialog.FILE_MODE_OPEN_FILE
	_file_dialog.access = FileDialog.ACCESS_FILESYSTEM
	_file_dialog.filters = PackedStringArray(["*.nes ; NES ROM"])
	_file_dialog.title = tr("选择 NES ROM")
	_file_dialog.file_selected.connect(_load_rom)

	get_window().files_dropped.connect(_on_files_dropped)

	var documents := OS.get_system_dir(OS.SYSTEM_DIR_DOCUMENTS)
	if documents != "":
		_file_dialog.current_dir = documents


func _open_rom_dialog() -> void:
	if _file_dialog != null:
		_file_dialog.popup_centered(Vector2i(1280, 860))


func _on_files_dropped(files: PackedStringArray) -> void:
	for file in files:
		if file.to_lower().ends_with(".nes"):
			_load_rom(file)
			return


func _load_rom(path: String) -> void:
	if _service == null:
		return

	if _service.LoadRom(path):
		_flash(tr("已装载：") + path.get_file())
	else:
		_flash(tr("装载失败：") + str(_service.LastError))

	_update_rom_menu_state()
	_update_status()


## 关闭当前 ROM：卸载卡带、清掉 ROM 信息，回到"空机"状态。
## 主界面不会因此停下来，只是没有画面也不再跑 CPU。
func _close_rom() -> void:
	if _service == null:
		return

	if not _service.HasRom:
		_flash(tr("当前没有装载 ROM"))
		return

	_service.CloseRom()

	# 卸载卡带之后 CPU 不再跑，必须主动清屏，否则上一帧会一直留在屏幕上
	_core.ClearDisplay()
	_flash(tr("已关闭 ROM"))
	_update_rom_menu_state()
	_update_status()


# ---------------------------------------------------------------------------
# 状态栏 / 对话框
# ---------------------------------------------------------------------------

func _toggle_pause() -> void:
	if _service != null:
		_service.TogglePause()

	_update_status()


func _update_status() -> void:
	if _status_label == null or _service == null:
		return

	if _flash_timer > 0.0 and _flash_message != "":
		_status_label.text = _flash_message
		return

	var snapshot: Dictionary = _service.GetSystemSnapshot()
	_last_frame = int(snapshot.get("frame", 0))

	# 分两行写：第一行是"装的是什么"（文件名可能很长），第二行是运行状态。
	# 状态栏的 Label 开了自动换行（main.tscn 里 autowrap_mode = 3），
	# 所以文件名再长也只会自己折行，不会顶出屏幕外面去。
	var head: Array[String] = []
	var rom_name := str(snapshot.get("rom_name", ""))
	head.append(rom_name if rom_name != "" else tr("未装载 ROM"))

	if bool(snapshot.get("has_rom", false)):
		head.append("Mapper %d (%s)" % [
			int(snapshot.get("mapper", -1)), str(snapshot.get("mapper_name", ""))])

	var tail: Array[String] = []
	tail.append("%.1f %s" % [float(snapshot.get("nes_fps", 0.0)), tr("NES 帧/秒")])
	tail.append("%d %s" % [int(snapshot.get("fps", 0)), tr("显示帧/秒")])
	tail.append("%s %d" % [tr("帧"), _last_frame])
	tail.append(tr("已暂停") if bool(snapshot.get("paused", false)) else tr("运行中"))

	# 性能分解：一个 NES 帧里"跑模拟"和"上传纹理"各占多少毫秒（60 帧/秒的预算只有 16.6ms）。
	# 有这两个数就能一眼看出卡顿是模拟器算不过来，还是画面/UI 那边拖的。
	if _core != null:
		tail.append("%s %.1f ms" % [tr("模拟"), _core.EmulationMicrosPerFrame / 1000.0])
		tail.append("%s %.1f ms" % [tr("上传"), _core.PresentMicrosPerFrame / 1000.0])

	_status_label.text = "%s\n%s" % [
		"    |    ".join(head),
		"    |    ".join(tail),
	]


func _flash(message: String) -> void:
	_flash_message = message
	_flash_timer = 4.0
	if _status_label != null:
		_status_label.text = message


func _take_snapshot() -> void:
	if _core == null:
		return

	var path := "user://frame-%d.png" % _last_frame
	var error: String = _core.SaveFramePng(path)
	if error == "":
		_flash(tr("已保存：") + ProjectSettings.globalize_path(path))
	else:
		_flash(tr("保存失败：") + error)


func _show_about() -> void:
	_show_dialog(tr("关于"), "\n".join([
		"godot-nes",
		"",
		tr("godot-nes — 用 Godot 4 + C# 实现的 NES 模拟器"),
		tr("当前进度：CPU / PPU / APU 已实现（Mapper 0，中英文界面）"),
		"",
		"godot-nes-emulator-plan.md",
	]))


func _show_shortcuts() -> void:
	var lines := [
		tr("手柄 1"),
		"  " + tr("方向键 / WASD") + "    " + tr("十字键"),
		"  X / K              A",
		"  Z / J              B",
		"  Enter              Start",
		"  Backspace          Select",
		"",
		tr("手柄 2"),
		"  " + tr("小键盘 8 / 2 / 4 / 6") + "    " + tr("十字键"),
		"  " + tr("小键盘 1") + "              A",
		"  " + tr("小键盘 3") + "              B",
		"  " + tr("小键盘 +") + "              Start",
		"  " + tr("小键盘 0") + "              Select",
		"  " + tr("第二个手柄直接用就行（会自动分给手柄 2）"),
		"",
		tr("功能"),
		"  O        " + tr("加载 ROM"),
		"  R        " + tr("复位"),
		"  P        " + tr("暂停 / 继续"),
		"  F10      " + tr("单步一条指令"),
		"  F1       " + tr("显示 / 隐藏状态栏"),
		"  F2       " + tr("CPU"),
		"  F3       " + tr("PPU"),
		"  F11      " + tr("全屏"),
		"  Esc      " + tr("退出"),
		"",
		tr("2 人游戏：在标题画面用十字键上 / 下选 2 PLAYER GAME，再按 Start 开始。"),
		tr("也可以把 .nes 文件直接拖进窗口。"),
	]
	_show_dialog(tr("快捷键"), "\n".join(lines))


func _show_dialog(title: String, text: String) -> void:
	var dialog := AcceptDialog.new()
	dialog.title = title
	dialog.dialog_text = text
	_apply_dark_theme(dialog)
	add_child(dialog)
	dialog.confirmed.connect(dialog.queue_free)
	dialog.canceled.connect(dialog.queue_free)
	dialog.close_requested.connect(dialog.queue_free)
	dialog.popup_centered()
