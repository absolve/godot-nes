extends DebugWindow

## ROM 信息窗口：把 .nes 头里解析出来的东西全部摊开显示
## —— 程序块（PRG）、图案块（CHR）、卡带参数、校验和、复位向量。
##
## 行标题固定，只有取值会变，所以行只建一次，之后只改 text。

const ROWS := [
	["文件", "file_name"],
	["大小", "file_size"],
	["格式", "format"],
	["Mapper", "mapper"],
	["子 mapper", "submapper"],
	["PRG ROM（程序块）", "prg"],
	["CHR ROM（图案块）", "chr"],
	["WRAM", "wram"],
	["名称表镜像", "mirroring"],
	["制式", "tv_system"],
	["平台", "console"],
	["电池存档", "battery"],
	["Trainer", "trainer"],
	["向量表", "vectors"],
	["CRC32（PRG+CHR）", "crc32"],
	["MD5（PRG+CHR）", "md5"],
	["映射器实现", "mapper_implemented"],
]

@onready var _path: Label = %PathLabel
@onready var _grid: GridContainer = %InfoGrid
@onready var _warnings: RichTextLabel = %WarningsLabel
@onready var _path_caption: Label = $Margin/VBox/PathCaption
@onready var _warnings_caption: Label = $Margin/VBox/WarningsCaption

var _values: Dictionary = {}
var _captions: Array[Label] = []


func _ready() -> void:
	_build_rows()
	title = tr("ROM 信息")
	super()


func _build_rows() -> void:
	for row in ROWS:
		var caption := Label.new()
		caption.text = tr(row[0])
		caption.theme_type_variation = "DimLabel"
		_grid.add_child(caption)
		_captions.append(caption)

		var value := Label.new()
		value.text = "--"
		value.theme_type_variation = "MonoLabel"
		_grid.add_child(value)
		_values[row[1]] = value


## 语言切换时把表头和标题重译一遍（值用最近一次快照原地重画）。
func _retranslate() -> void:
	super()
	title = tr("ROM 信息")

	for index in ROWS.size():
		if index < _captions.size():
			_captions[index].text = tr(ROWS[index][0])

	_warnings_caption.text = tr("解析提示")
	_path_caption.text = tr("文件路径")
	_refresh(_last_snapshot)


func _refresh(snapshot: Dictionary) -> void:
	var rom: Dictionary = snapshot.get("rom", {})

	if rom.is_empty() or not bool(rom.get("has_rom", false)):
		_path.text = tr("(未装载 ROM)")
		for key in _values:
			_values[key].text = "--"
		_warnings.text = ""
		return

	_path.text = str(rom.get("file_path", ""))

	for key in _values:
		_values[key].text = _format_value(key, rom)

	var warnings: Array = rom.get("warnings", [])
	if warnings.is_empty():
		_warnings.text = "[color=#6a7080]%s[/color]" % tr("头部解析没有发现问题")
	else:
		var text := ""
		for warning in warnings:
			text += "[color=#e0a030]• %s[/color]\n" % str(warning)
		_warnings.text = text


func _format_value(key: String, rom: Dictionary) -> String:
	match key:
		"file_size":
			return _with_thousands(int(rom.get("file_size", 0))) + " " + tr("字节")

		"mapper":
			return "%d (%s)" % [int(rom.get("mapper", -1)), str(rom.get("mapper_name", ""))]

		"submapper":
			if str(rom.get("format", "")) != "NES 2.0":
				return tr("—（iNES 1.0 没有这个字段）")
			return str(int(rom.get("submapper", 0)))

		"prg":
			return tr("%d KB（%d x 16KB）") % [int(rom.get("prg_kb", 0)), int(rom.get("prg_banks", 0))]

		"chr":
			if bool(rom.get("uses_chr_ram", false)):
				var text := tr("无 CHR ROM，用 %d KB CHR RAM") % int(rom.get("chr_ram_kb", 0))
				var chr_nvram := int(rom.get("chr_nvram_kb", 0))
				if chr_nvram > 0:
					text += tr("（另有电池 CHR-NVRAM %d KB）") % chr_nvram
				return text
			return tr("%d KB（%d x 8KB）") % [int(rom.get("chr_kb", 0)), int(rom.get("chr_banks", 0))]

		"wram":
			var wram := int(rom.get("wram_kb", 0))
			var nvram := int(rom.get("nvram_kb", 0))
			if nvram > 0:
				return tr("%d KB（另有电池 NVRAM %d KB）") % [wram, nvram]
			return "%d KB" % wram

		"battery", "trainer":
			return tr("有") if bool(rom.get(key, false)) else tr("无")

		"vectors":
			return "NMI $%04X   RESET $%04X   IRQ $%04X" % [
				int(rom.get("nmi_vector", 0)),
				int(rom.get("reset_vector", 0)),
				int(rom.get("irq_vector", 0)),
			]

		"mapper_implemented":
			return tr("已实现") if bool(rom.get("mapper_implemented", false)) else tr("未实现（计划阶段 7）")

		_:
			return tr(str(rom.get(key, "--")))


## 给文件大小加千位分隔符，几 MB 的 ROM 一眼能读。
func _with_thousands(value: int) -> String:
	var text := str(value)
	var result := ""
	var count := 0
	for i in range(text.length() - 1, -1, -1):
		result = text[i] + result
		count += 1
		if count % 3 == 0 and i > 0:
			result = "," + result
	return result
