extends Window
class_name DebugWindow

## 所有调试窗口的基类。
##
## 负责四件事：
##   1. 「关闭」只是隐藏，不 free —— 窗口位置和状态都留着，再打开是秒开；
##   2. 可见时才向 DebugHub 订阅数据，隐藏时立刻退订（没有窗口开就零开销）；
##   3. 不可见时收到快照也不刷新任何东西；
##   4. 语言切换时通知子类重译。tscn 里写死的文字其实由引擎在排版时自动翻（atr()），
##      但那样 `.text` 读出来还是原语言，无头测试没法断言 —— 所以子类用 _static_texts()
##      把「节点唯一名 -> 中文原文」交上来，基类显式 tr() 一遍写回去。
##
## 另外两件事：
##   * 缓存最近一次快照：切语言时子类可以立刻原地重画，不用等下一次 12 Hz 刷新；
##   * 跟随主窗口的界面缩放（见 _follow_main_window_scale）—— 独立 OS 窗口不会自动
##     跟着主窗口的 canvas_items 拉伸走，不跟的话主窗口放大之后这里的字会显得很小。

## 最近一次快照（可能是空的：没装 ROM 时就是空字典）。
var _last_snapshot: Dictionary = {}

## tscn 里写的设计尺寸。跟随主窗口缩放时按它成比例放大，避免内容被裁掉。
var _design_size := Vector2i.ZERO


func _ready() -> void:
	close_requested.connect(hide)
	visibility_changed.connect(_on_visibility_changed)
	DebugHub.snapshot_updated.connect(_on_snapshot_updated)
	UiSettings.locale_changed.connect(_on_locale_changed)

	# 样式来自项目主题（project.godot 的 gui/theme/custom），不用自己挂。
	# 但独立窗口的**缩放**得自己跟：见下面这个函数。
	_design_size = size
	_follow_main_window_scale()
	get_tree().root.size_changed.connect(_follow_main_window_scale)

	# 场景里默认就是隐藏的；这里再确保一次，并同步订阅状态。
	if visible:
		hide()

	# 启动时先按当前语言翻一遍（系统语言可能是英文）。
	_last_snapshot = DebugHub.pull_now()
	_retranslate()


## 让调试窗口和主窗口**看起来一样大**。
##
## 主窗口用 canvas_items 拉伸：窗口比设计分辨率（1024x768）大多少，界面就放大多少倍。
## 独立 `Window` 不吃这套，`content_scale_factor` 默认是 1 —— 于是主窗口放大到 1.5 倍时，
## 调试窗口里的字看上去就比主界面小一圈。这里手动把主窗口的缩放比抄过来，
## 并按同样的比例放大窗口尺寸（不然逻辑尺寸不变、内容会被裁掉）。
func _follow_main_window_scale() -> void:
	if _design_size == Vector2i.ZERO:
		return

	var design := Vector2(
		float(ProjectSettings.get_setting("display/window/size/viewport_width", 1024)),
		float(ProjectSettings.get_setting("display/window/size/viewport_height", 768)))
	var root := get_tree().root
	var scale := maxf(1.0, float(root.size.y) / design.y)

	if is_equal_approx(scale, content_scale_factor):
		return

	content_scale_factor = scale
	size = Vector2i(Vector2(_design_size) * scale)


func _on_visibility_changed() -> void:
	if visible:
		DebugHub.register_consumer()
		_on_snapshot_updated(DebugHub.pull_now())
	else:
		DebugHub.unregister_consumer()


func _on_snapshot_updated(snapshot: Dictionary) -> void:
	_last_snapshot = snapshot
	if visible:
		_refresh(snapshot)


func _on_locale_changed(_code: String) -> void:
	_retranslate()


## 子类覆写：把快照渲染到界面上。基类保证只在窗口可见时调用。
func _refresh(_snapshot: Dictionary) -> void:
	pass


## 子类覆写：把 tscn 里写死的文字登记成「节点唯一名 -> 中文原文」。
func _static_texts() -> Dictionary:
	return {}


## 子类通常不需要覆写，覆写的话记得先调 super()。
func _retranslate() -> void:
	var texts := _static_texts()
	for node_name in texts:
		var label := get_node_or_null("%" + str(node_name))
		if label is Label:
			label.text = tr(str(texts[node_name]))
