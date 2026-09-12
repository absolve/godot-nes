namespace GodotNes.Core;

/// <summary>
/// Mapper 23（Konami VRC2b / VRC4e）：Contra (Japan)、Wai Xing Zhan Shi 这类在用。
///
/// VRC2/VRC4 这一族的变体差别**只在"哪根地址线选寄存器/半字节"**，所以照 FCEUX 的
/// VRC24Write 做法：先把地址做一次 A0/A1 交换（reg1mask=0x15、reg2mask=0x2a），
/// 之后按统一布局解码：
///
///   $8000  PRG 8KB → $8000        $A000  PRG 8KB → $A000
///   $9000  镜像（bit0：0=垂直、1=水平）
///   $B000  CHR0 低 4 位            $B001  CHR0 高 4 位
///   $B002  CHR1 低 4 位            $B003  CHR1 高 4 位
///   $C000… 同理依次是 CHR2..CHR7
///   $C000 固定倒数第二块、$E000 固定最后一块
///
/// 没实现 VRC4 的 $F000 扫描线中断 —— Contra 是 VRC2 卡用不到；真遇到需要的 ROM 再补。
/// </summary>
public sealed class Vrc2Mapper : Mapper
{
	private const int PrgBank8K = 0x2000;
	private const int ChrBank1K = 0x0400;

	private readonly byte[] _prg = new byte[2];
	private readonly byte[] _chr = new byte[8];
	private readonly int[] _chrOffsets = new int[8];
	private readonly int[] _prgOffsets = new int[4];
	private Mirroring? _mirroring;

	public Vrc2Mapper(Cartridge cartridge)
		: base(cartridge)
	{
		UpdateOffsets();
	}

	public override string Name => "VRC2b (mapper 23)";

	public override Mirroring? DynamicMirroring => _mirroring;

	public override void Reset()
	{
		System.Array.Clear(_chr);
		_prg[0] = 0;
		_prg[1] = 1;
		_mirroring = null;                 // 还没写过镜像寄存器 → 用卡带头里的值
		UpdateOffsets();
	}

	public override byte CpuRead(ushort address)
	{
		int slot = (address - 0x8000) >> 13;
		return Cartridge.PrgRom[_prgOffsets[slot] + (address & (PrgBank8K - 1))];
	}

	public override void CpuWrite(ushort address, byte value)
	{
		if (address < 0x8000)
		{
			return;
		}

		// VRC2b/VRC4e 的地址线交换：A0 与 A1 互换
		int a = (address & 0xF000)
			| ((address & 0x2A) != 0 ? 2 : 0)
			| ((address & 0x15) != 0 ? 1 : 0);

		if (a is >= 0xB000 and <= 0xE003)
		{
			// CHR：A1 选第几个 1KB bank、A0 选写低半字节还是高半字节
			int slot = ((a >> 1) & 1) | ((a - 0xB000) >> 11);
			int shift = (a & 1) << 2;
			_chr[slot] = (byte)((_chr[slot] & (0xF0 >> shift)) | ((value & 0x0F) << shift));
			UpdateOffsets();
			return;
		}

		switch (a & 0xF003)
		{
			case 0x8000:
				_prg[0] = (byte)(value & 0x1F);
				break;
			case 0xA000:
				_prg[1] = (byte)(value & 0x1F);
				break;
			case 0x9000:
				_mirroring = (value & 0x01) != 0 ? Mirroring.Horizontal : Mirroring.Vertical;
				break;
			default:
				return;                     // $9002/$9003 的 regcmd、$F000 的中断寄存器都不管
		}

		UpdateOffsets();
	}

	public override byte PpuRead(ushort address)
	{
		int slot = (address & 0x1FFF) >> 10;
		return ReadChr(_chrOffsets[slot] + (address & (ChrBank1K - 1)));
	}

	public override void PpuWrite(ushort address, byte value)
	{
		int slot = (address & 0x1FFF) >> 10;
		WriteChr(_chrOffsets[slot] + (address & (ChrBank1K - 1)), value);
	}

	private void UpdateOffsets()
	{
		_prgOffsets[0] = PrgBank(_prg[0], PrgBank8K);
		_prgOffsets[1] = PrgBank(_prg[1], PrgBank8K);
		_prgOffsets[2] = PrgBank(-2, PrgBank8K);
		_prgOffsets[3] = PrgBank(-1, PrgBank8K);

		for (int i = 0; i < 8; i++)
		{
			_chrOffsets[i] = ChrBank(_chr[i], ChrBank1K);
		}
	}
}
