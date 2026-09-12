using System;

namespace GodotNes.Core;

/// <summary>6502 状态寄存器 P 的位定义。</summary>
[Flags]
public enum StatusFlags : byte
{
	Carry = 1 << 0,
	Zero = 1 << 1,
	InterruptDisable = 1 << 2,
	Decimal = 1 << 3,
	Break = 1 << 4,
	Unused = 1 << 5,
	Overflow = 1 << 6,
	Negative = 1 << 7,
}
