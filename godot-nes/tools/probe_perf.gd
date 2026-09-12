extends Node

## 性能探针：量"模拟器有没有被真实时间节流住"，并顺便看音频缓冲有没有被喂爆。
##
##   godot --path <项目> res://tools/probe_perf.tscn -- <rom>
##
## 为什么要带窗口跑：无头（--headless）时 EmulatorCore 故意不节流（测试要的是确定的帧数），
## 所以"速度对不对"只能在带窗口的模式下量。
## 判断标准：不管显示器是 60Hz 还是 165Hz，NES 帧率都应该贴着 60.0988；
## 音频被丢掉的样本数应该一直是 0（一直涨 = 喂得太快 = 听感上的噪音）。

const MEASURE_MS := 3000

## 测量期间累计 Godot 报的 delta（就是 EmulatorCore 用来节流的那份时间）。
var _delta_sum := 0.0
var _display_frames := 0
var _measuring := false


func _process(delta: float) -> void:
	if _measuring:
		_delta_sum += delta
		_display_frames += 1

func _ready() -> void:
	var args := OS.get_cmdline_user_args()
	if args.size() < 1:
		push_error("[probe_perf] 用法：-- <rom>")
		get_tree().quit(1)
		return

	var rom: String = args[0]

	var main_scene: PackedScene = load("res://scene/main.tscn")
	var main: Node = main_scene.instantiate()
	add_child(main)
	await get_tree().process_frame

	var service: Node = get_node_or_null("/root/EmulatorService")
	if service == null:
		push_error("[probe_perf] 找不到 EmulatorService")
		get_tree().quit(1)
		return

	print("[probe_perf] display refresh = %.2f Hz，Godot vsync 模式 = %d"
		% [DisplayServer.screen_get_refresh_rate(), DisplayServer.window_get_vsync_mode()])

	if not service.LoadRom(rom):
		push_error("[probe_perf] 装载失败：%s" % service.LastError)
		get_tree().quit(1)
		return

	# 先把开机那几帧跳过去
	for i in 60:
		await get_tree().process_frame

	# 第一轮先不开"按部件统计"：那份统计自己每指令要取两三次时间戳，会把总时间抬高。
	var core_node: Node = get_tree().root.find_child("EmulatorCore", true, false)
	if core_node != null:
		core_node.ProfileComponents = false

	await _measure(service, "默认（vsync 开）")

	# 第二轮再打开，看"模拟"里 CPU / PPU / APU 各占多少
	if core_node != null:
		core_node.ProfileComponents = true

	# 关掉 vsync 再量一次：显示帧会跑得比 60 快得多，
	# 如果模拟器没按墙钟节流，这里的 NES 帧率就会跟着飙上去。
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_DISABLED)
	for i in 30:
		await get_tree().process_frame
	await _measure(service, "vsync 关")

	# 再停掉音频播放量一次：用来判断"每秒一次的卡顿"是不是音频驱动造成的
	var audio: Node = main.get_node_or_null("%NesAudioPlayer")
	if audio != null:
		audio.call("stop")
		for i in 30:
			await get_tree().process_frame
		await _measure(service, "vsync 关 + 音频停")

	if audio != null:
		var dropped := int(audio.DroppedSamples)
		var verdict2 := "[OK]" if dropped == 0 else "[注意]"
		print("[probe_perf] %s 音频：因缓冲满而丢掉的样本 %d 个" % [verdict2, dropped])

	# ---- GDScript 侧开销 ----
	# 把每个显示帧都要跑（或经常要跑）的脚本函数单独计时，看它们值不值这么多时间。
	print("[probe_perf] ---- GDScript 侧开销 ----")
	_measure_script("状态栏 _update_status()", Callable(main, "_update_status"), 60)
	var label: Label = main.get_node_or_null("Ui/Root/StatusBar/StatusLabel")
	if label != null:
		_measure_script("状态栏 Label 重排", Callable(label, "get_minimum_size"), 2000)

	# ---- 打开调试窗口之后再量一次 ----
	# 调试窗口是最可疑的脚本开销来源：开着的时候每个显示帧都要跨语言取快照 + 刷一堆控件。
	DisplayServer.window_set_vsync_mode(DisplayServer.VSYNC_ENABLED)
	var cpu_window: Window = main.get_node_or_null("DebugWindows/CpuWindow")
	if cpu_window != null:
		cpu_window.popup_centered()
		for i in 60:
			await get_tree().process_frame
		await _measure(service, "开着 CPU 调试窗口")
		cpu_window.hide()

	get_tree().quit()


func _measure(service: Node, label: String) -> void:
	_delta_sum = 0.0
	_display_frames = 0
	_measuring = true

	var t0 := Time.get_ticks_msec()
	var f0 := int(service.GetSystemSnapshot()["frame"])
	var wall := 0
	var worst_gap := 0
	var last := t0
	var stall_count := 0
	var stall_total := 0
	while wall < MEASURE_MS:
		await get_tree().process_frame
		var now := Time.get_ticks_msec()
		var gap := now - last
		worst_gap = maxi(worst_gap, gap)
		if gap > 50:
			stall_count += 1
			stall_total += gap
		last = now
		wall = now - t0

	_measuring = false

	var frames := int(service.GetSystemSnapshot()["frame"]) - f0
	var seconds := (Time.get_ticks_msec() - t0) / 1000.0
	var fps := frames / seconds
	var verdict := "[OK]" if abs(fps - 60.0988) < 1.0 else "[注意]"
	print("[probe_perf] %s 墙钟 %.2f 秒 → %d 个 NES 帧 = %.2f 帧/秒 %s（真机 60.0988）"
		% [label, seconds, frames, fps, verdict])
	print("[probe_perf]    Godot 累计 delta %.2f 秒（差 %.2f 秒），显示帧 %d 个 = %.2f 帧/秒"
		% [_delta_sum, seconds - _delta_sum, _display_frames, _display_frames / seconds])
	print("[probe_perf]    >50ms 的卡顿 %d 次共 %d ms，单帧最长 %d ms" % [stall_count, stall_total, worst_gap])

	# 一个 NES 帧的开销拆开看：模拟（CPU/PPU/APU）多少、上传纹理多少。
	# 60 帧/秒的预算只有 16.6ms，两个数加起来超了就是它导致掉帧。
	var core: Node = get_tree().root.find_child("EmulatorCore", true, false)
	if core != null:
		var emu := float(core.EmulationMicrosPerFrame) / 1000.0
		var blit := float(core.PresentMicrosPerFrame) / 1000.0
		var total := emu + blit
		var tag := "[OK]" if total < 16.6 else "[注意]"
		print("[probe_perf]    %s 每帧开销：模拟 %.2f ms + 上传纹理 %.2f ms = %.2f ms（预算 16.6 ms）"
			% [tag, emu, blit, total])
		if bool(core.ProfileComponents) and emu > 0.1:
			print("[probe_perf]       CPU %.2f ms + PPU %.2f ms + APU %.2f ms"
				% [float(core.ProfiledCpuMsPerFrame), float(core.ProfiledPpuMsPerFrame),
					float(core.ProfiledApuMsPerFrame)])


## 量一次 GDScript 侧的开销（状态栏刷新、调试窗口刷新），用来回答
## "卡顿是不是 Godot 这边 GDScript 太慢"。
func _measure_script(label: String, callable: Callable, iterations: int) -> void:
	var t0 := Time.get_ticks_usec()
	for i in iterations:
		callable.call()
	var micros := float(Time.get_ticks_usec() - t0) / iterations
	print("[probe_perf]    %s：每次 %.1f µs（%d 次平均）" % [label, micros, iterations])
