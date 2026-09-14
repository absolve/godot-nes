extends Node

## 按键配置。
##
## 配置文件的存放位置分两种：
##   - **编辑器里跑**：`user://godot-nes-input.cfg`（Godot 的用户数据目录，不污染工程目录）
##   - **打包成 exe 之后**：exe **同一个目录**下的 `godot-nes-input.cfg` —— 用户拿文本编辑器
##     改完重启就生效，不用重新打包。
##
## 两种情况都会读写：文件不在就自动写一份带默认值的出来。
##
## 文件长这样（键名用 Godot 的写法，和 `OS.get_keycode_string()` 一致）：
##
##     [player1]
##     up=W
##     down=S
##     left=A
##     right=D
##     b=J
##     a=K
##     select=U
##     start=I
##
##     [player2]
##     ...
##
##     [system]
##     four_score=false      ; 插了四人分插器就改 true（这时 3/4 号手柄才有效）
##
## 3/4 号手柄的键位只有这里（或下面的 DEFAULT_KEYS）能改 —— 键盘上四个人本来就挤，
## 默认给的是 T/F/G/H 一套，4 号位用小键盘。
##

const CONFIG_NAME = "godot-nes-input.cfg"

## 键位布局版本。**每次改默认键位就把它 +1**。
##
## 为什么要这个：配置文件一旦生成就会一直覆盖默认值，改了默认布局老用户也拿不到
## —— 本项目踩过这个坑（1P/2P 键位重排之后，旧配置文件让新键位完全看不出效果）。
## 版本对不上时用新默认值重写一份（会丢掉手工改过的键位，这是有意的：布局换了）。
const LAYOUT_VERSION := 2

## 一个手柄的 8 个键，顺序和 DEFAULT_KEYS 里的键码一一对应。
const BUTTONS: Array[String] = ["up", "down", "left", "right", "b", "a", "select", "start"]

## 3/4 号手柄的默认键位（1/2 号直接用 project.godot 里已经配好的那套）。
const DEFAULT_KEYS := {
	3: [KEY_T, KEY_G, KEY_F, KEY_H, KEY_R, KEY_V, KEY_C, KEY_B],
	4: [KEY_KP_8, KEY_KP_2, KEY_KP_4, KEY_KP_6, KEY_KP_3, KEY_KP_1, KEY_KP_0, KEY_KP_ADD],
}

## 是否插了四人分插器（Four Score）。由配置文件决定，启动时推给模拟器。
var four_score := false


func _ready() -> void:
	_ensure_actions()

	var path = config_path()
	var config = ConfigFile.new()
	if config.load(path) != OK:
		_write_default_config(path)             # 第一次运行：写一份默认配置供用户修改
		return

	if int(config.get_value("system", "layout_version", 0)) < LAYOUT_VERSION:
		print("[godot-nes] 键位布局已更新（配置是 v%d，当前 v%d），用新默认值重写配置"
			% [int(config.get_value("system", "layout_version", 0)), LAYOUT_VERSION])
		_write_default_config(path)
		return

	_apply(config)
	print("[godot-nes] 已应用按键配置：", path)


## 配置文件路径：编辑器里放 `user://`，打包后放 exe 同目录。
func config_path() -> String:
	if OS.has_feature("editor"):
		return "user://" + CONFIG_NAME

	return OS.get_executable_path().get_base_dir().path_join(CONFIG_NAME)


## 动作名：1 号位叫 `nes_up`，2/3/4 号位叫 `nes2_up` / `nes3_up` / `nes4_up`。
func action_name(player: int, button: String) -> String:
	return "nes_%s" % button if player == 1 else "nes%d_%s" % [player, button]


## 补上缺失的动作；只给"一个事件都没有"的动作填默认键，不动已有的绑定。
func _ensure_actions() -> void:
	for player in [1, 2, 3, 4]:
		for i in BUTTONS.size():
			var action := action_name(player, BUTTONS[i])
			if not InputMap.has_action(action):
				InputMap.add_action(action)

			if InputMap.action_get_events(action).is_empty() and DEFAULT_KEYS.has(player):
				_add_key(action, DEFAULT_KEYS[player][i])


func _apply(config: ConfigFile) -> void:
	for player in [1, 2, 3, 4]:
		var section := "player%d" % player
		for button in BUTTONS:
			var names := str(config.get_value(section, button, ""))
			if names == "":
				continue
			var keycode := OS.find_keycode_from_string(names)
			if keycode == KEY_NONE:
				push_warning("[godot-nes] 按键配置里认不出这个键名，已跳过：%s.%s = %s" % [section, button, names])
				continue
			_set_key(action_name(player, button), keycode)

	four_score = bool(config.get_value("system", "four_score", false))


## 只替换键盘绑定，手柄（joypad）绑定原样保留 —— 配置文件管的是键盘。
func _set_key(action: String, keycode: int) -> void:
	for event in InputMap.action_get_events(action):
		if event is InputEventKey:
			InputMap.action_erase_event(action, event)
	_add_key(action, keycode)


func _add_key(action: String, keycode: int) -> void:
	var event := InputEventKey.new()
	event.physical_keycode = keycode
	InputMap.action_add_event(action, event)


## 把当前生效的绑定写一份到 exe 同目录（默认配置，用户照着改）。
func _write_default_config(path: String) -> void:
	var config := ConfigFile.new()
	for player in [1, 2, 3, 4]:
		var section := "player%d" % player
		for button in BUTTONS:
			var keycode := _current_keycode(action_name(player, button))
			config.set_value(section, button, OS.get_keycode_string(keycode))
	config.set_value("system", "four_score", four_score)
	config.set_value("system", "layout_version", LAYOUT_VERSION)

	var err := config.save(path)
	if err == OK:
		print("[godot-nes] 已写出默认按键配置：", path, "（改完重启生效）")
	else:
		push_warning("[godot-nes] 按键配置写不出去（%s）：%s" % [error_string(err), path])


func _current_keycode(action: String) -> int:
	for event in InputMap.action_get_events(action):
		if event is InputEventKey:
			return event.physical_keycode
	return KEY_NONE
