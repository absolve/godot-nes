using System.Text;

namespace GodotNes.Core;

/// <summary>
/// MOS 6502（2A03 版）CPU 核心。
///
/// 实现方式照搬 jsnes / fogleman-nes 的思路，保持简洁：
///   OpcodeTable[opcode] 给出「操作 + 寻址模式 + 基础周期」，
///   Step() 里分三步：取指 → 算操作数地址（顺带判断跨页）→ 执行。
///   寻址和执行各一个 switch，指令表里没有重复逻辑。
///
/// 2A03 没有 BCD 模式：CLD / SED 照样翻转 D 标志位，但 ADC / SBC 不看它。
/// 周期数对齐 nesdev 官方表：基础周期在表里，跨页附加周期和分支附加周期在运行时加。
/// </summary>
public sealed partial class Cpu
{
	private readonly Bus _bus;

	public Cpu(Bus bus)
	{
		_bus = bus ?? throw new System.ArgumentNullException(nameof(bus));
	}

	// ------------------------------------------------------------------ 寄存器

	public byte A { get; set; }

	public byte X { get; set; }

	public byte Y { get; set; }

	public byte SP { get; set; }

	/// <summary>状态寄存器 P。</summary>
	public byte P { get; set; }

	public ushort PC { get; set; }

	/// <summary>累计周期数，调度器按它推进帧。</summary>
	public long Cycles { get; set; }

	/// <summary>最近一次执行的操作码（调试显示用）。</summary>
	public byte LastOpcode { get; private set; }

	/// <summary>是否撞到了未实现的非法指令。撞到就停住，等阶段 7 再补。</summary>
	public bool HitIllegalOpcode { get; private set; }

	/// <summary>复位向量 $FFFC/$FFFD 的内容。</summary>
	public ushort ResetVector => _bus.Read16(0xFFFC);

	// ------------------------------------------------------------------ 复位与执行

	/// <summary>复位：SP = $FD，I = 1，PC 取复位向量，耗 7 个周期。</summary>
	public void Reset()
	{
		A = 0;
		X = 0;
		Y = 0;
		SP = 0xFD;
		P = (byte)(StatusFlags.InterruptDisable | StatusFlags.Unused);
		PC = _bus.Read16(0xFFFC);
		Cycles = 7;
		LastOpcode = 0;
		HitIllegalOpcode = false;
	}

	/// <summary>执行一条指令，返回消耗的周期数。</summary>
	public int Step()
	{
		// 撞过非法指令就一直停着，别拿着已经不对的状态继续往下跑。
		if (HitIllegalOpcode)
		{
			return 0;
		}

		LastOpcode = _bus.Read(PC++);

		Op op = OpcodeTable.Ops[LastOpcode];
		if (op.Ins == Instruction.Illegal)
		{
			HitIllegalOpcode = true;
			return 2;
		}

		ushort address = ResolveAddress(op.Mode, out bool pageCrossed);

		int extra = Execute(op, address);
		if (pageCrossed && HasPageCrossPenalty(op.Ins))
		{
			extra++;
		}

		int cycles = op.Cycles + extra;
		Cycles += cycles;

		// $4014 OAM DMA：这次写之后 CPU 要停 513 个周期（写在奇数周期上时 514 个）。
		// 停顿期间 CPU 不取指，但 PPU / APU 照常跑 —— 所以这段周期数要算进返回值里，
		// 由 NesConsole 拿去推进 PPU / APU。少了它游戏时序会整体偏，SMB 的关卡数据
		// 就会错位（踩过：标题画面能画出来，但马里奥的精灵根本不进 OAM）。
		if (_bus.OamDmaStallPending)
		{
			_bus.OamDmaStallPending = false;
			int stall = 513 + ((Cycles & 1) != 0 ? 1 : 0);
			Cycles += stall;
			return cycles + stall;
		}

		return cycles;
	}

	// ------------------------------------------------------------------ 寻址

	/// <summary>
	/// 算出操作数的有效地址。<paramref name="pageCrossed"/> 表示索引寻址是否跨了 256 字节页
	/// —— 真机会因此多花一个周期，但只有"读"类指令要算这笔账，由调用方决定加不加。
	/// </summary>
	private ushort ResolveAddress(AddrMode mode, out bool pageCrossed)
	{
		pageCrossed = false;

		switch (mode)
		{
			case AddrMode.Imp:
			case AddrMode.Acc:
				return 0;

			case AddrMode.Imm:
				return PC++;                       // 操作数地址就是下一个字节

			case AddrMode.Zp:
				return ReadByte();

			case AddrMode.ZpX:
				return (byte)(ReadByte() + X);     // 零页内回绕，不会跨到 $01xx

			case AddrMode.ZpY:
				return (byte)(ReadByte() + Y);

			case AddrMode.Abs:
				return ReadWord();

			case AddrMode.AbsX:
				return Indexed(ReadWord(), X, out pageCrossed);

			case AddrMode.AbsY:
				return Indexed(ReadWord(), Y, out pageCrossed);

			case AddrMode.IndX:
			{
				byte pointer = (byte)(ReadByte() + X);
				return ReadWordZeroPage(pointer);
			}

			case AddrMode.IndY:
			{
				ushort baseAddress = ReadWordZeroPage(ReadByte());
				return Indexed(baseAddress, Y, out pageCrossed);
			}

			case AddrMode.Ind:
			{
				// JMP (xxxx)：真机 bug —— 指针低字节是 $xxFF 时，高字节从同页回绕取，
				// 不会进位到下一页。模拟器必须照抄，否则个别游戏会跑飞。
				ushort pointer = ReadWord();
				byte low = _bus.Read(pointer);
				byte high = _bus.Read((ushort)((pointer & 0xFF00) | ((pointer + 1) & 0x00FF)));
				return (ushort)(low | (high << 8));
			}

			case AddrMode.Rel:
			{
				// 分支目标 = 操作数之后的 PC + 有符号偏移。
				sbyte offset = (sbyte)ReadByte();
				return (ushort)(PC + offset);
			}

			default:
				return 0;
		}
	}

	private static ushort Indexed(ushort baseAddress, byte index, out bool pageCrossed)
	{
		ushort address = (ushort)(baseAddress + index);
		pageCrossed = (baseAddress & 0xFF00) != (address & 0xFF00);
		return address;
	}

	/// <summary>零页指针：高字节在 $00xx 内回绕，不会读到 $0100。</summary>
	private ushort ReadWordZeroPage(byte pointer)
	{
		byte low = _bus.Read(pointer);
		byte high = _bus.Read((byte)(pointer + 1));
		return (ushort)(low | (high << 8));
	}

	/// <summary>
	/// 只有"读"类指令在索引寻址跨页时才多花一个周期。
	/// 写指令（STA/STX/STY）和读改写指令（ASL/INC…）的周期数里已经含了那一个周期，
	/// 再给它们加就会多算。
	/// </summary>
	private static bool HasPageCrossPenalty(Instruction ins) => ins is
		Instruction.Lda or Instruction.Ldx or Instruction.Ldy or
		Instruction.And or Instruction.Ora or Instruction.Eor or
		Instruction.Adc or Instruction.Sbc or Instruction.Cmp;

	// ------------------------------------------------------------------ 内存与栈

	private byte ReadByte() => _bus.Read(PC++);

	private ushort ReadWord()
	{
		byte low = ReadByte();
		byte high = ReadByte();
		return (ushort)(low | (high << 8));
	}

	private void Push(byte value) => _bus.Write((ushort)(0x0100 + SP--), value);

	private byte Pop() => _bus.Read((ushort)(0x0100 + ++SP));

	// ------------------------------------------------------------------ 标志位

	public bool GetFlag(StatusFlags flag) => (P & (byte)flag) != 0;

	public void SetFlag(StatusFlags flag, bool value)
	{
		P = value ? (byte)(P | (byte)flag) : (byte)(P & ~(byte)flag);
	}

	/// <summary>按结果更新 Z 和 N —— 大部分指令都要做，单独抽出来。</summary>
	private void SetZeroNegative(byte value)
	{
		SetFlag(StatusFlags.Zero, value == 0);
		SetFlag(StatusFlags.Negative, (value & 0x80) != 0);
	}

	// ------------------------------------------------------------------ 中断

	/// <summary>NMI（PPU 的 VBlank）。</summary>
	public void Nmi() => Interrupt(0xFFFA);

	/// <summary>IRQ（mapper / APU 帧计数器）。I 置位时忽略。</summary>
	public void Irq()
	{
		if (!GetFlag(StatusFlags.InterruptDisable))
		{
			Interrupt(0xFFFE);
		}
	}

	/// <summary>硬件中断：压 PC 和 P（B 位为 0），跳到向量，耗 7 个周期。</summary>
	private void Interrupt(ushort vector)
	{
		Push((byte)(PC >> 8));
		Push((byte)PC);
		Push((byte)((P | (byte)StatusFlags.Unused) & ~(byte)StatusFlags.Break));

		SetFlag(StatusFlags.InterruptDisable, true);
		PC = _bus.Read16(vector);
		Cycles += 7;
	}

	// ------------------------------------------------------------------ 反汇编 / trace

	/// <summary>
	/// 指令长度（字节）。由寻址模式推出来，不用单独存表；**只有 BRK 是特例** ——
	/// 它实际占 1 字节，但会连着跳过后面那个补齐字节，所以对外按 2 字节算
	/// （反汇编和 trace 都要这个数，nestest 的日志也是这么显示的）。
	/// </summary>
	public static int SizeOf(byte opcode)
	{
		Op op = OpcodeTable.Ops[opcode];
		if (op.Ins == Instruction.Brk)
		{
			return 2;
		}

		return op.Mode switch
		{
			AddrMode.Imp or AddrMode.Acc => 1,
			AddrMode.Abs or AddrMode.AbsX or AddrMode.AbsY or AddrMode.Ind => 3,
			_ => 2,
		};
	}

	/// <summary>
	/// 把"执行这条指令之前"的 CPU 状态格式化成一行，用来和参照实现逐行对比。
	/// 格式：PC OP B1 B2 A X Y P SP CYC（不足 3 字节的位置用 .. 占位）。
	/// 周期数取指令开始时的值，和 nestest.log 的习惯一致。
	/// </summary>
	public string FormatTrace()
	{
		byte opcode = _bus.Read(PC);
		int size = SizeOf(opcode);

		var sb = new StringBuilder(48);
		sb.Append(PC.ToString("X4")).Append(' ').Append(opcode.ToString("X2"));

		for (int i = 1; i < 3; i++)
		{
			sb.Append(' ');
			sb.Append(i < size ? _bus.Read((ushort)(PC + i)).ToString("X2") : "..");
		}

		sb.Append(' ').Append(A.ToString("X2"));
		sb.Append(' ').Append(X.ToString("X2"));
		sb.Append(' ').Append(Y.ToString("X2"));
		sb.Append(' ').Append(P.ToString("X2"));
		sb.Append(' ').Append(SP.ToString("X2"));
		sb.Append(' ').Append(Cycles);
		return sb.ToString();
	}
}
