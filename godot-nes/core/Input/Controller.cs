namespace GodotNes.Core;

/// <summary>NES 手柄按键。枚举值就是 $4016 移位寄存器里的位序（A 先出）。</summary>
public enum NesButton : byte
{
	A = 0,
	B = 1,
	Select = 2,
	Start = 3,
	Up = 4,
	Down = 5,
	Left = 6,
	Right = 7,
}

/// <summary>
/// 标准手柄（4021 移位寄存器）。
/// Godot 侧每帧把按键位写进 <see cref="Buttons"/>，
/// 游戏通过写 $4016（选通）+ 读 $4016/$4017（移位）获取。
/// </summary>
public sealed class Controller : IInputSource
{
	private byte _shift;

	/// <summary>按下的按键位掩码：bit0=A，bit1=B，……，bit7=Right。</summary>
	public byte Buttons { get; set; }

	/// <summary>选通状态（$4016 写入值的最低位）。</summary>
	public bool Strobe { get; private set; }

	public void SetButton(NesButton button, bool pressed)
	{
		byte mask = (byte)(1 << (int)button);
		Buttons = pressed ? (byte)(Buttons | mask) : (byte)(Buttons & ~mask);
	}

	public bool IsPressed(NesButton button) => (Buttons & (1 << (int)button)) != 0;

	/// <summary>写 $4016：bit0 = 1 时不断重载，= 0 时开始逐位输出。</summary>
	public void Write(byte value)
	{
		Strobe = (value & 0x01) != 0;
		if (Strobe)
		{
			_shift = Buttons;
		}
	}

	/// <summary>读 $4016/$4017：返回当前位并右移一位，高位补 1（读完 8 位后一直返回 1）。</summary>
	public byte Read()
	{
		if (Strobe)
		{
			return (byte)(Buttons & 0x01);
		}

		byte result = (byte)(_shift & 0x01);
		_shift = (byte)((_shift >> 1) | 0x80);
		return result;
	}

	public byte ReadButtons(int port) => Buttons;
}
