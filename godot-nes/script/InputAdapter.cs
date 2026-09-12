using Godot;
using GodotNes.Core;

namespace GodotNes.Game;

/// <summary>
/// 把 Godot 的输入翻译成 NES 手柄位。
/// <see cref="EmulatorCore.StepAndPresent"/> 每帧在跑模拟之前调用一次 <see cref="PushTo"/>，
/// 这样调用顺序是确定的，不依赖节点树的 _Process 顺序。
///
/// 注意：这个方法收 <see cref="NesConsole"/>，属于"非 Variant 兼容参数"，
/// 所以 GDScript 看不到它（has_method 返回 false）—— 正好，输入采样就只能留在 C# 内部。
/// 键位 / 手柄映射在 project.godot 的 [input] 段里定义。
/// </summary>
public partial class InputAdapter : Node
{
	private static readonly (string Action, NesButton Button)[] Mapping =
	{
		("nes_a", NesButton.A),
		("nes_b", NesButton.B),
		("nes_select", NesButton.Select),
		("nes_start", NesButton.Start),
		("nes_up", NesButton.Up),
		("nes_down", NesButton.Down),
		("nes_left", NesButton.Left),
		("nes_right", NesButton.Right),
	};

	/// <summary>手柄 2 的映射：键盘走小键盘，手柄走第二个设备（project.godot 里 device=1）。</summary>
	private static readonly (string Action, NesButton Button)[] Mapping2 =
	{
		("nes2_a", NesButton.A),
		("nes2_b", NesButton.B),
		("nes2_select", NesButton.Select),
		("nes2_start", NesButton.Start),
		("nes2_up", NesButton.Up),
		("nes2_down", NesButton.Down),
		("nes2_left", NesButton.Left),
		("nes2_right", NesButton.Right),
	};

	/// <summary>把手柄 1 / 手柄 2 的当前状态写进总线。</summary>
	public void PushTo(NesConsole console)
	{
		Push(console.Bus.Controller1, Mapping);
		Push(console.Bus.Controller2, Mapping2);
	}

	private static void Push(Controller pad, (string Action, NesButton Button)[] mapping)
	{
		for (int i = 0; i < mapping.Length; i++)
		{
			(string action, NesButton button) = mapping[i];
			pad.SetButton(button, Input.IsActionPressed(action));
		}
	}
}
