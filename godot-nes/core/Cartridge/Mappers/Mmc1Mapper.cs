namespace GodotNes.Core;

/// <summary>
/// Mapper 1 / MMC1（SxROM）：串行写入的 5 位移位寄存器 + PRG/CHR 换 bank + 动态镜像。
/// 《塞尔达传说》《银河战士》《忍者龙剑传》《最终幻想》都是它。
///
/// 写寄存器的协议是这套芯片最大的特点：CPU 一次只送 1 个 bit（写 $8000-$FFFF 的 bit0），
/// 连送 5 次才组成一个值，**由第 5 次写落在哪个地址段决定写的是哪个寄存器**。
/// 写的时候 bit7 置 1 是"复位移位寄存器"（同时把 PRG 模式强制回 3）。
///
/// 已知简化（写在明处，别当 bug）：
///   - 不做"连续两个 CPU 周期写会丢"的忽略规则（真机有这个毛病，游戏都避开了）；
///   - PRG RAM 写保护位（$A001 之类）忽略，我们的 WRAM 一直可写；
///   - 512KB 卡带拿 CHR 寄存器高位当 PRG 高位（SXROM 扩展）没做，
///     所以超过 256KB PRG 的 MMC1 卡带只能用到低 4 位对应的 bank。
/// </summary>
public sealed class Mmc1Mapper : Mapper
{
	private const int PrgBank16K = 0x4000;
	private const int ChrBank4K = 0x1000;

	/// <summary>移位寄存器：bit4 是"下一位要写进来的位置"，bit0 = 1 表示已经收满 5 位。</summary>
	private byte _shift = 0x10;

	/// <summary>
	/// 控制寄存器（$8000-$9FFF）：镜像 2 位、PRG 模式 2 位、CHR 模式 1 位。
	/// 上电值取 FCEUX 的惯例 $1F：PRG 模式 3（最后一块固定在 $C000）、镜像水平、CHR 4KB 模式。
	/// 真机上这几个位是"不确定"的，所有游戏开头都会先写一遍，所以取哪套只影响复位到那之前。
	/// </summary>
	private byte _control = 0x1F;

	private byte _chrBank0;
	private byte _chrBank1;
	private byte _prgBank;

	private int _prgOffset0;
	private int _prgOffset1;
	private int _chrOffset0;
	private int _chrOffset1;

	public Mmc1Mapper(Cartridge cartridge)
		: base(cartridge)
	{
		UpdateOffsets();
	}

	public override string Name => "MMC1 (SxROM)";

	public override Mirroring? DynamicMirroring =>
		Cartridge.HasFourScreen
			? Mirroring.FourScreen
			: (_control & 0x03) switch
			{
				0 => Mirroring.SingleScreenLower,
				1 => Mirroring.SingleScreenUpper,
				2 => Mirroring.Vertical,
				_ => Mirroring.Horizontal,
			};

	public override byte CpuRead(ushort address) =>
		address < 0xC000
			? Cartridge.PrgRom[_prgOffset0 + (address - 0x8000)]
			: Cartridge.PrgRom[_prgOffset1 + (address - 0xC000)];

	public override void CpuWrite(ushort address, byte value)
	{
		if ((value & 0x80) != 0)
		{
			// bit7 = 1：清空移位寄存器，并把 PRG 模式拉回 3（最后一块固定在 $C000，
			// 也就是复位向量那一块），这样即使移位同步乱掉，程序也不会跑飞。
			_shift = 0x10;
			WriteControl((byte)(_control | 0x0C));
			return;
		}

		bool complete = (_shift & 0x01) != 0;
		_shift = (byte)((_shift >> 1) | ((value & 0x01) << 4));
		if (!complete)
		{
			return;
		}

		byte data = _shift;
		_shift = 0x10;

		if (address < 0xA000)
		{
			WriteControl(data);
		}
		else if (address < 0xC000)
		{
			_chrBank0 = data;
			UpdateOffsets();
		}
		else if (address < 0xE000)
		{
			_chrBank1 = data;
			UpdateOffsets();
		}
		else
		{
			_prgBank = (byte)(data & 0x0F);
			UpdateOffsets();
		}
	}

	public override byte PpuRead(ushort address) =>
		address < 0x1000
			? ReadChr(_chrOffset0 + address)
			: ReadChr(_chrOffset1 + (address - 0x1000));

	public override void PpuWrite(ushort address, byte value) =>
		WriteChr((address < 0x1000 ? _chrOffset0 + address : _chrOffset1 + (address - 0x1000)), value);

	public override void Reset()
	{
		_shift = 0x10;
		_control = 0x1F;
		_chrBank0 = 0;
		_chrBank1 = 0;
		_prgBank = 0;
		UpdateOffsets();
	}

	private void WriteControl(byte value)
	{
		_control = value;
		UpdateOffsets();
	}

	private void UpdateOffsets()
	{
		switch ((_control >> 2) & 0x03)
		{
			case 0:
			case 1:
				// 32KB 模式：$8000 和 $C000 是连续两块，bank 号的 bit0 被忽略
				_prgOffset0 = PrgBank(_prgBank & 0xFE, PrgBank16K);
				_prgOffset1 = PrgBank(_prgBank | 0x01, PrgBank16K);
				break;

			case 2:
				// 第一块固定在 $8000，$C000 可换
				_prgOffset0 = PrgBank(0, PrgBank16K);
				_prgOffset1 = PrgBank(_prgBank, PrgBank16K);
				break;

			default:
				// 最后一块固定在 $C000（复位向量在这里），$8000 可换 —— 复位后的默认模式
				_prgOffset0 = PrgBank(_prgBank, PrgBank16K);
				_prgOffset1 = PrgBank(-1, PrgBank16K);
				break;
		}

		if ((_control & 0x10) != 0)
		{
			// 4KB 模式：两块各换各的
			_chrOffset0 = ChrBank(_chrBank0, ChrBank4K);
			_chrOffset1 = ChrBank(_chrBank1, ChrBank4K);
		}
		else
		{
			// 8KB 模式：用 chrBank0 的低位当奇偶，凑成连续 8KB
			_chrOffset0 = ChrBank(_chrBank0 & 0xFE, ChrBank4K);
			_chrOffset1 = ChrBank(_chrBank0 | 0x01, ChrBank4K);
		}
	}
}
