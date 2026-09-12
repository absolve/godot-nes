extends DebugWindow

## PPU 窗口：寄存器 / 状态位 / 调色板 / 两张 pattern table 预览。
##
## pattern table 预览是 C# 节点（script/PatternTablePreview.cs）：CHR 解码属于"重数据"，
## 放在 C# 侧读就不用把字节数组跨语言传过来。GDScript 只负责摆位置，然后调无参的 Refresh()。
## 预览刷新比 DebugHub 慢（2 秒一次都嫌快，这里 0.5 秒）：解码 256 个图块没必要每帧来一遍。

const PREVIEW_REFRESH_MS := 500

const STATUS_FLAGS := [
	["vblank", "VBlank"],
	["sprite0_hit", "精灵0命中"],
	["sprite_overflow", "精灵溢出"],
	["nmi_enabled", "NMI 使能"],
	["rendering", "渲染开"],
	["sprite_8x16", "8x16 精灵"],
]

@onready var _ctrl: Label = %CtrlValue
@onready var _mask: Label = %MaskValue
@onready var _status: Label = %StatusValue
@onready var _oam_addr: Label = %OamValue
@onready var _vram_addr: Label = %VramValue
@onready var _increment: Label = %IncrementValue
@onready var _bg_base: Label = %BgBaseValue
@onready var _sprite_base: Label = %SpriteBaseValue
@onready var _flags: RichTextLabel = %FlagsLabel
@onready var _palette_grid: GridContainer = %PaletteGrid
@onready var _table0 = %Table0
@onready var _table1 = %Table1

## tscn 里写死的表头（节点唯一名 -> 中文原文），交给基类统一 tr()。
const STATIC_TEXTS := {
	"VramCaption": "VRAM 地址",
	"IncrementCaption": "增量",
	"BgBaseCaption": "背景表",
	"SpriteBaseCaption": "精灵表",
	"FlagsCaption": "状态与开关",
	"PaletteCaption": "调色板 RAM（$3F00-$3F1F，色块下方是颜色号）",
	"PreviewCaption": "Pattern table（卡带 CHR，4 级灰阶）",
	"Table0Caption": "表 $0000",
	"Table1Caption": "表 $1000",
}

## 32 个色块，索引对齐 $3F00-$3F1F。
var _swatches: Array[ColorRect] = []
var _service: Node = null
var _last_preview_ms := 0


func _static_texts() -> Dictionary:
	return STATIC_TEXTS


## 状态位名字和「名称表镜像」那行要重译，重画一遍就行。
func _retranslate() -> void:
	super()
	_refresh(_last_snapshot)


func _ready() -> void:
	_service = get_node_or_null("/root/EmulatorService")
	_build_palette()
	super()


func _build_palette() -> void:
	# C# 侧返回的是 Color[] / int[]，跨到 GDScript 就是 PackedColorArray / PackedInt32Array。
	var palette = _service.GetNesPalette() if _service != null else PackedColorArray()

	for index in 32:
		var cell := VBoxContainer.new()
		cell.add_theme_constant_override("separation", 1)

		var swatch := ColorRect.new()
		swatch.custom_minimum_size = Vector2(24, 18)
		swatch.color = palette[index] if index < palette.size() else Color.BLACK
		cell.add_child(swatch)
		_swatches.append(swatch)

		var caption := Label.new()
		caption.text = "%02X" % index
		# 跟着主题走：小一号、暗一点，不用写死字号
		caption.theme_type_variation = "DimLabel"
		cell.add_child(caption)

		_palette_grid.add_child(cell)


func _refresh(snapshot: Dictionary) -> void:
	var ppu: Dictionary = snapshot.get("ppu", {})
	if ppu.is_empty():
		return

	_ctrl.text = "%02X" % int(ppu.get("ctrl", 0))
	_mask.text = "%02X" % int(ppu.get("mask", 0))
	_status.text = "%02X" % int(ppu.get("status", 0))
	_oam_addr.text = "%02X" % int(ppu.get("oam_addr", 0))
	_vram_addr.text = "%04X" % int(ppu.get("vram_addr", 0))
	_increment.text = str(int(ppu.get("increment", 1)))
	_bg_base.text = "$%04X" % int(ppu.get("bg_pattern_base", 0))
	_sprite_base.text = "$%04X" % int(ppu.get("sprite_pattern_base", 0))

	var flags_text := ""
	for flag in STATUS_FLAGS:
		var on := bool(ppu.get(flag[0], false))
		var color := "#7fd67f" if on else "#4a4f5c"
		flags_text += "[color=%s]%s[/color]    " % [color, tr(flag[1])]
	# 镜像给的是**当前生效**的值（MMC1/MMC3/AxROM 游戏运行时会变），
	# C# 侧给的是中文原文，这里再 tr() 一次才能跟着语言走。
	flags_text += "\n[color=#6a7080]" + tr("名称表镜像：%s") % tr(str(ppu.get("mirroring", "--"))) + "[/color]"
	_flags.text = flags_text

	_refresh_palette()
	_refresh_previews()


func _refresh_palette() -> void:
	if _service == null:
		return

	var palette = _service.GetNesPalette()
	var ram = _service.GetPaletteRam()

	for index in _swatches.size():
		if index >= ram.size():
			break
		var color_index := int(ram[index]) & 0x3F
		if color_index < palette.size():
			_swatches[index].color = palette[color_index]


func _refresh_previews() -> void:
	# pattern table 解码有点重，没必要跟着 12 Hz 的快照走。
	var now := Time.get_ticks_msec()
	if now - _last_preview_ms < PREVIEW_REFRESH_MS:
		return
	_last_preview_ms = now

	if _table0 != null:
		_table0.Refresh()
	if _table1 != null:
		_table1.Refresh()
