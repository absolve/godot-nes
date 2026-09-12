extends Node

## 临时工具：调色板索引帧（--dump-frame-indices 的 61440 字节）→ PNG。
##
##   # 单张转换（放大 3 倍便于肉眼看）
##   godot --headless --path <项目> res://tools/idx2png.tscn -- to-png <输入.idx> <输出.png> [倍数]
##
##   # 两张对比：**按 RGB 比**，不一样的像素涂成品红，一样的灰掉
##   godot --headless --path <项目> res://tools/idx2png.tscn -- diff <a.idx> <b.idx> <输出.png> [倍数]
##
## 为什么对比要按 RGB：NES 调色板里有重复颜色（比如 $20 和 $30 都是纯白），
## 参照实现是把 RGBA 反查回索引的，遇到重复色会报成较小的那个索引 ——
## 直接比索引会看到一大堆"假不同"。用完这个文件可以删。

const WIDTH := 256
const HEIGHT := 240


func _ready() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() < 2:
		push_error("[idx2png] 用法见文件头注释")
		get_tree().quit(1)
		return

	match args[0]:
		"to-png":
			if args.size() < 3:
				_fail("to-png 需要 <输入.idx> <输出.png> [倍数]")
				return
			var scale := 1
			if args.size() >= 4:
				scale = maxi(1, int(args[3]))
			_to_png(args[1], args[2], scale)
		"diff":
			if args.size() < 4:
				_fail("diff 需要 <a.idx> <b.idx> <输出.png> [倍数]")
				return
			var scale := 1
			if args.size() >= 5:
				scale = maxi(1, int(args[4]))
			_diff(args[1], args[2], args[3], scale)
		_:
			_fail("未知模式 %s" % args[0])


func _fail(message: String) -> void:
	push_error("[idx2png] " + message)
	get_tree().quit(1)


func _load_indices(path: String) -> PackedByteArray:
	var raw := FileAccess.get_file_as_bytes(path)
	if raw.size() != WIDTH * HEIGHT:
		push_error("[idx2png] %s 应该是 61440 字节，实际 %d" % [path, raw.size()])
		get_tree().quit(1)
		return PackedByteArray()

	return raw


func _palette() -> Array:
	var service: Node = get_node_or_null("/root/EmulatorService")
	if service == null:
		push_error("[idx2png] 找不到 EmulatorService")
		get_tree().quit(1)
		return []

	return service.GetNesPalette()


func _to_png(input: String, output: String, scale: int) -> void:
	var raw := _load_indices(input)
	var palette := _palette()
	var width := WIDTH * scale
	var height := HEIGHT * scale
	var pixels := PackedByteArray()
	pixels.resize(width * height * 4)

	for y in height:
		for x in width:
			var index: int = raw[(y / scale) * WIDTH + (x / scale)] & 0x3F
			_write(pixels, (y * width + x) * 4, palette[index])

	_save(pixels, width, height, output)


func _diff(a_path: String, b_path: String, output: String, scale: int) -> void:
	var a := _load_indices(a_path)
	var b := _load_indices(b_path)
	var palette := _palette()
	var width := WIDTH * scale
	var height := HEIGHT * scale
	var pixels := PackedByteArray()
	pixels.resize(width * height * 4)

	var different := 0
	var min_x := WIDTH
	var max_x := -1
	var min_y := HEIGHT
	var max_y := -1

	for y in height:
		for x in width:
			var i := (y / scale) * WIDTH + (x / scale)
			var ca: Color = palette[a[i] & 0x3F]
			var cb: Color = palette[b[i] & 0x3F]
			var same := is_equal_approx(ca.r, cb.r) and is_equal_approx(ca.g, cb.g) \
				and is_equal_approx(ca.b, cb.b)
			if same:
				# 一样的像素：调暗当背景
				_write(pixels, (y * width + x) * 4, Color(ca.r * 0.35, ca.g * 0.35, ca.b * 0.35))
			else:
				_write(pixels, (y * width + x) * 4, Color(1.0, 0.0, 1.0))
				different += 1
				var px := x / scale
				var py := y / scale
				min_x = mini(min_x, px)
				max_x = maxi(max_x, px)
				min_y = mini(min_y, py)
				max_y = maxi(max_y, py)

	_save(pixels, width, height, output)
	print("[idx2png] RGB 不同的像素：%d 个（缩放后 %d），范围 x=%d..%d y=%d..%d"
		% [different / (scale * scale), different, min_x, max_x, min_y, max_y])


func _write(pixels: PackedByteArray, offset: int, color: Color) -> void:
	pixels[offset + 0] = int(color.r * 255.0)
	pixels[offset + 1] = int(color.g * 255.0)
	pixels[offset + 2] = int(color.b * 255.0)
	pixels[offset + 3] = 255


func _save(pixels: PackedByteArray, width: int, height: int, output: String) -> void:
	var image := Image.create_from_data(width, height, false, Image.FORMAT_RGBA8, pixels)
	var error := image.save_png(output)
	if error != OK:
		_fail("保存失败：%s" % error)
		return

	print("[idx2png] 已写出 %s（%dx%d）" % [output, width, height])
	get_tree().quit()
