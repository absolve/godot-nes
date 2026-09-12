extends DebugWindow

## CPU 窗口：寄存器 / 标志位 / 栈顶。
## 骨架在 cpu_window.tscn 里，这里只负责把快照填进去。

## 状态寄存器的位序（P 的最高位在前），和 6502 的手册一致。
const FLAG_ORDER := [
	["flag_n", "N"], ["flag_v", "V"], ["flag_u", "-"], ["flag_b", "B"],
	["flag_d", "D"], ["flag_i", "I"], ["flag_z", "Z"], ["flag_c", "C"],
]

@onready var _pc: Label = %PcValue
@onready var _a: Label = %AValue
@onready var _x: Label = %XValue
@onready var _y: Label = %YValue
@onready var _sp: Label = %SpValue
@onready var _p: Label = %PValue
@onready var _cycles: Label = %CyclesValue
@onready var _flags: RichTextLabel = %FlagsLabel
@onready var _stack: Label = %StackLabel
@onready var _status: RichTextLabel = %StatusLabel

## tscn 里写死的表头（节点唯一名 -> 中文原文），交给基类统一 tr()。
const STATIC_TEXTS := {
	"FlagsCaption": "标志位 P（亮 = 置位）",
	"StackCaption": "栈顶（SP+1 起 8 字节）",
}


func _static_texts() -> Dictionary:
	return STATIC_TEXTS


## 标志位名字（N/V/B…）两种语言一样，但状态那两句话要重译。
func _retranslate() -> void:
	super()
	_refresh(_last_snapshot)


func _refresh(snapshot: Dictionary) -> void:
	var cpu: Dictionary = snapshot.get("cpu", {})
	if cpu.is_empty():
		return

	_pc.text = "%04X" % int(cpu.get("pc", 0))
	_a.text = "%02X" % int(cpu.get("a", 0))
	_x.text = "%02X" % int(cpu.get("x", 0))
	_y.text = "%02X" % int(cpu.get("y", 0))
	_sp.text = "%02X" % int(cpu.get("sp", 0))
	_p.text = "%02X" % int(cpu.get("p", 0))
	_cycles.text = str(int(cpu.get("cycles", 0)))

	var flags_text := ""
	for flag in FLAG_ORDER:
		var on := bool(cpu.get(flag[0], false))
		var color := "#7fd67f" if on else "#4a4f5c"
		flags_text += "[color=%s]%s[/color]  " % [color, flag[1]]
	_flags.text = flags_text

	_stack.text = str(cpu.get("stack", "--"))

	var opcode := int(cpu.get("illegal_opcode", -1))
	if opcode >= 0:
		_status.text = "[color=#e0a030]%s[/color]" % tr("遇到非法指令 $%02X（非官方指令，阶段 7 再补）") % opcode
	else:
		_status.text = "[color=#6a7080]%s[/color]" % tr("正常：只用了官方 56 条指令")
