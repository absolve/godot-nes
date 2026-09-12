namespace GodotNes.Core;

/// <summary>
/// 官方 56 条指令的实现（Cpu 的另一半，见 Cpu.cs）。
///
/// 约定：Execute 的返回值是**额外周期数**，只有分支跳转会产生；
/// 基础周期数在 OpcodeTable 里，跨页附加周期在 Step 里加。
/// 所以这里只关心"结果对不对"，不用管周期。
/// </summary>
public sealed partial class Cpu
{
	private int Execute(in Op op, ushort address)
	{
		switch (op.Ins)
		{
			// ---------------------------------------------------------- 载入
			case Instruction.Lda: A = _bus.Read(address); SetZeroNegative(A); break;
			case Instruction.Ldx: X = _bus.Read(address); SetZeroNegative(X); break;
			case Instruction.Ldy: Y = _bus.Read(address); SetZeroNegative(Y); break;

			// ---------------------------------------------------------- 存储
			case Instruction.Sta: _bus.Write(address, A); break;
			case Instruction.Stx: _bus.Write(address, X); break;
			case Instruction.Sty: _bus.Write(address, Y); break;

			// ---------------------------------------------------------- 寄存器传送
			case Instruction.Tax: X = A; SetZeroNegative(X); break;
			case Instruction.Tay: Y = A; SetZeroNegative(Y); break;
			case Instruction.Txa: A = X; SetZeroNegative(A); break;
			case Instruction.Tya: A = Y; SetZeroNegative(A); break;
			case Instruction.Tsx: X = SP; SetZeroNegative(X); break;
			case Instruction.Txs: SP = X; break;                       // 不影响标志位

			// ---------------------------------------------------------- 栈
			case Instruction.Pha: Push(A); break;
			case Instruction.Pla: A = Pop(); SetZeroNegative(A); break;
			case Instruction.Php: Push((byte)(P | BreakAndUnused)); break;
			case Instruction.Plp: P = (byte)((Pop() | (byte)StatusFlags.Unused) & ~(byte)StatusFlags.Break); break;

			// ---------------------------------------------------------- 逻辑
			case Instruction.And: A &= _bus.Read(address); SetZeroNegative(A); break;
			case Instruction.Ora: A |= _bus.Read(address); SetZeroNegative(A); break;
			case Instruction.Eor: A ^= _bus.Read(address); SetZeroNegative(A); break;

			case Instruction.Bit:
			{
				byte value = _bus.Read(address);
				SetFlag(StatusFlags.Zero, (A & value) == 0);
				SetFlag(StatusFlags.Overflow, (value & 0x40) != 0);    // bit6 进 V
				SetFlag(StatusFlags.Negative, (value & 0x80) != 0);    // bit7 进 N
				break;
			}

			// ---------------------------------------------------------- 算术
			case Instruction.Adc: AddWithCarry(_bus.Read(address)); break;
			case Instruction.Sbc: AddWithCarry((byte)~_bus.Read(address)); break;   // SBC 就是取反操作数的 ADC

			// ---------------------------------------------------------- 比较
			case Instruction.Cmp: Compare(A, _bus.Read(address)); break;
			case Instruction.Cpx: Compare(X, _bus.Read(address)); break;
			case Instruction.Cpy: Compare(Y, _bus.Read(address)); break;

			// ---------------------------------------------------------- 移位 / 循环
			case Instruction.Asl: Shift(op.Mode, address, ShiftLeft); break;
			case Instruction.Lsr: Shift(op.Mode, address, ShiftRight); break;
			case Instruction.Rol: Shift(op.Mode, address, RotateLeft); break;
			case Instruction.Ror: Shift(op.Mode, address, RotateRight); break;

			// ---------------------------------------------------------- 自增 / 自减
			case Instruction.Inc:
			{
				byte value = (byte)(_bus.Read(address) + 1);
				_bus.Write(address, value);
				SetZeroNegative(value);
				break;
			}

			case Instruction.Dec:
			{
				byte value = (byte)(_bus.Read(address) - 1);
				_bus.Write(address, value);
				SetZeroNegative(value);
				break;
			}

			case Instruction.Inx: X++; SetZeroNegative(X); break;
			case Instruction.Iny: Y++; SetZeroNegative(Y); break;
			case Instruction.Dex: X--; SetZeroNegative(X); break;
			case Instruction.Dey: Y--; SetZeroNegative(Y); break;

			// ---------------------------------------------------------- 跳转
			case Instruction.Jmp: PC = address; break;

			case Instruction.Jsr:
			{
				// 压入的是"操作数最后一字节"的地址，RTS 时 +1 才是下一条指令
				ushort returnAddress = (ushort)(PC - 1);
				Push((byte)(returnAddress >> 8));
				Push((byte)returnAddress);
				PC = address;
				break;
			}

			case Instruction.Rts:
			{
				byte low = Pop();
				byte high = Pop();
				PC = (ushort)(((high << 8) | low) + 1);
				break;
			}

			case Instruction.Rti:
			{
				P = (byte)((Pop() | (byte)StatusFlags.Unused) & ~(byte)StatusFlags.Break);
				byte low = Pop();
				byte high = Pop();
				PC = (ushort)((high << 8) | low);
				break;
			}

			case Instruction.Brk:
			{
				// BRK 只占 1 字节，但会把跟在后面的补齐字节一起跳过，所以压入的是 PC + 2
				PC++;
				Push((byte)(PC >> 8));
				Push((byte)PC);
				Push((byte)(P | BreakAndUnused));
				SetFlag(StatusFlags.InterruptDisable, true);
				PC = _bus.Read16(0xFFFE);
				break;
			}

			// ---------------------------------------------------------- 分支
			case Instruction.Bcc: if (!GetFlag(StatusFlags.Carry)) { return Branch(address); } break;
			case Instruction.Bcs: if (GetFlag(StatusFlags.Carry)) { return Branch(address); } break;
			case Instruction.Beq: if (GetFlag(StatusFlags.Zero)) { return Branch(address); } break;
			case Instruction.Bne: if (!GetFlag(StatusFlags.Zero)) { return Branch(address); } break;
			case Instruction.Bmi: if (GetFlag(StatusFlags.Negative)) { return Branch(address); } break;
			case Instruction.Bpl: if (!GetFlag(StatusFlags.Negative)) { return Branch(address); } break;
			case Instruction.Bvc: if (!GetFlag(StatusFlags.Overflow)) { return Branch(address); } break;
			case Instruction.Bvs: if (GetFlag(StatusFlags.Overflow)) { return Branch(address); } break;

			// ---------------------------------------------------------- 标志位
			case Instruction.Clc: SetFlag(StatusFlags.Carry, false); break;
			case Instruction.Sec: SetFlag(StatusFlags.Carry, true); break;
			case Instruction.Cli: SetFlag(StatusFlags.InterruptDisable, false); break;
			case Instruction.Sei: SetFlag(StatusFlags.InterruptDisable, true); break;
			case Instruction.Clv: SetFlag(StatusFlags.Overflow, false); break;
			case Instruction.Cld: SetFlag(StatusFlags.Decimal, false); break;   // 2A03 上只翻标志位，没有实际作用
			case Instruction.Sed: SetFlag(StatusFlags.Decimal, true); break;

			case Instruction.Nop:
			default:
				break;
		}

		return 0;
	}

	/// <summary>压栈时 B 和 U 都置位（PHP / BRK 用）。</summary>
	private const byte BreakAndUnused = (byte)(StatusFlags.Break | StatusFlags.Unused);

	/// <summary>ADC。2A03 没有 BCD 模式，所以完全不用看 D 标志位。</summary>
	private void AddWithCarry(byte value)
	{
		int carry = GetFlag(StatusFlags.Carry) ? 1 : 0;
		int sum = A + value + carry;

		SetFlag(StatusFlags.Carry, sum > 0xFF);
		// 溢出：两个同号数相加，结果变成异号
		SetFlag(StatusFlags.Overflow, (~(A ^ value) & (A ^ sum) & 0x80) != 0);

		A = (byte)sum;
		SetZeroNegative(A);
	}

	private void Compare(byte register, byte value)
	{
		SetFlag(StatusFlags.Carry, register >= value);   // 无借位 → C = 1
		SetZeroNegative((byte)(register - value));
	}

	/// <summary>分支：跳转本身多 1 个周期，跨页再多 1 个。</summary>
	private int Branch(ushort target)
	{
		bool pageCrossed = (PC & 0xFF00) != (target & 0xFF00);
		PC = target;
		return pageCrossed ? 2 : 1;
	}

	/// <summary>移位类指令：累加器模式直接改 A，其余模式读改写内存。</summary>
	private void Shift(AddrMode mode, ushort address, System.Func<byte, byte> operation)
	{
		if (mode == AddrMode.Acc)
		{
			A = operation(A);
			return;
		}

		_bus.Write(address, operation(_bus.Read(address)));
	}

	private byte ShiftLeft(byte value)
	{
		SetFlag(StatusFlags.Carry, (value & 0x80) != 0);
		byte result = (byte)(value << 1);
		SetZeroNegative(result);
		return result;
	}

	private byte ShiftRight(byte value)
	{
		SetFlag(StatusFlags.Carry, (value & 0x01) != 0);
		byte result = (byte)(value >> 1);          // 高位补 0
		SetZeroNegative(result);
		return result;
	}

	private byte RotateLeft(byte value)
	{
		bool carryIn = GetFlag(StatusFlags.Carry);
		SetFlag(StatusFlags.Carry, (value & 0x80) != 0);
		byte result = (byte)((value << 1) | (carryIn ? 1 : 0));
		SetZeroNegative(result);
		return result;
	}

	private byte RotateRight(byte value)
	{
		bool carryIn = GetFlag(StatusFlags.Carry);
		SetFlag(StatusFlags.Carry, (value & 0x01) != 0);
		byte result = (byte)((value >> 1) | (carryIn ? 0x80 : 0));
		SetZeroNegative(result);
		return result;
	}
}
