extends Node

## 界面设置：目前只管**语言**。
##
## 样式（配色、字号、样式盒、字体变体）**全部在 `theme/dark_theme.tres` 里**，
## 通过 project.godot 的 `gui/theme/custom` 挂在项目主题上 —— Godot 会把它当成全局兜底主题，
## 主窗口、每个独立 `Window`、对话框里的控件都吃得到，不需要任何代码去"挂主题"。
## 改样式直接开 Godot 的主题编辑器改那个 .tres，不用碰代码。
##
## 这个 autoload 只做两件事：
##   1. 启动时按系统语言选中文/英文；
##   2. 切语言时发 `locale_changed` 信号，让代码里拼出来的文字（菜单、状态栏、表头）重译一遍。
##
## 为什么语言不能也交给引擎自动翻译：Godot 会在**排版时**自动翻译控件的文字（`atr()`），
## 但那样 `.text` 读出来还是原语言，无头测试没法断言；而且代码里拼出来的
## `tr("...%d...") % x` 这类带格式的串本来就得自己重译。所以统一显式 `tr()` + 重译一遍。

signal locale_changed(code: String)

## 视口清屏色（画面四周的留白）。Theme 管不到视口底色，值放在主题里，
## 这里只是取出来给 `RenderingServer` 用 —— 换配色仍然只改 theme/dark_theme.tres。
const THEME_PATH := "res://theme/dark_theme.tres"

var language := "zh"

const _SUPPORTED := ["zh", "en"]


func _ready() -> void:
	# 默认跟随系统：是英文环境就用英文，其余一律中文
	var system_locale := OS.get_locale()
	_apply_locale("en" if system_locale.begins_with("en") else "zh")


## 窗口底色：从项目主题里取（找不到就退回浏览器深色模式那个深灰蓝）。
func window_background() -> Color:
	var project_theme: Theme = ThemeDB.get_project_theme()
	if project_theme != null and project_theme.has_color("window_background", "NesTheme"):
		return project_theme.get_color("window_background", "NesTheme")

	return Color("202124")


## 切换界面语言。code 只支持 "zh" / "en"。
func set_language(code: String) -> void:
	if not _SUPPORTED.has(code) or code == language:
		return
	_apply_locale(code)


func _apply_locale(code: String) -> void:
	language = code
	TranslationServer.set_locale(code)
	locale_changed.emit(code)


## 取当前语言在菜单里显示的勾选状态。
func is_language(code: String) -> bool:
	return language == code
