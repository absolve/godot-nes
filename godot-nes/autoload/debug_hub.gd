extends Node

## 调试数据的"节流阀"。
##
## C# 的核心类型（NesConsole / Cpu / Ppu）对 GDScript 完全不可见，调试数据只能由
## EmulatorService 拍成 Dictionary 交出来。这个 autoload 负责按固定频率去拉，
## 再广播给所有可见的调试窗口。
##
## 关键点：没有任何调试窗口打开时，一次都不拉 —— 调试开销精确为 0。

signal snapshot_updated(snapshot: Dictionary)

## ROM 信息变化时发一次（装载新 ROM 才会变，不需要跟着 12 Hz 走）。
signal rom_updated(rom: Dictionary)

## 12 Hz。UI 够用，而跨语言调用的开销可以忽略。
const PULL_INTERVAL := 1.0 / 12.0

## 最近一次快照，窗口刚打开时可以直接拿来显示，不用等下一个周期。
var snapshot: Dictionary = {}

## ROM 详细信息，只在 RomLoaded 之后重新拉取。
var rom: Dictionary = {}

var _timer := 0.0
var _consumers := 0
var _service: Node = null

func _ready() -> void:
	_service = get_node_or_null("/root/EmulatorService")
	if _service == null:
		push_error("[godot-nes] DebugHub 找不到 EmulatorService 自动加载，调试窗口不会有数据。")
		return

	# EmulatorService 是 C# 节点，[Signal] 会生成同名的 C# 事件，GDScript 可以直接 connect。
	if _service.has_signal("RomLoaded"):
		_service.RomLoaded.connect(_on_rom_loaded)
	if _service.has_signal("RomClosed"):
		_service.RomClosed.connect(_on_rom_closed)

	rom = _pull_rom()

func _process(delta: float) -> void:
	if _consumers <= 0 or _service == null:
		return

	_timer += delta
	if _timer < PULL_INTERVAL:
		return
	_timer = 0.0

	snapshot = _service.GetDebugSnapshot()
	snapshot["rom"] = rom
	snapshot_updated.emit(snapshot)

## 调试窗口变为可见时调用。
func register_consumer() -> void:
	_consumers += 1

## 调试窗口隐藏时调用；归零后 DebugHub 完全静默。
func unregister_consumer() -> void:
	_consumers = maxi(0, _consumers - 1)

func consumer_count() -> int:
	return _consumers

## 立刻拉一次快照（窗口刚打开时用，避免先显示一帧空数据）。
func pull_now() -> Dictionary:
	if _service != null:
		snapshot = _service.GetDebugSnapshot()
		snapshot["rom"] = rom
	return snapshot

## 直接从 EmulatorService 调方法（状态栏这类不需要节流的地方用）。
func service() -> Node:
	return _service

func _pull_rom() -> Dictionary:
	if _service == null:
		return {}
	return _service.GetRomInfo()

func _on_rom_loaded(_path: String, _description: String) -> void:
	rom = _pull_rom()
	rom_updated.emit(rom)

func _on_rom_closed() -> void:
	rom = _pull_rom()
	rom_updated.emit(rom)
