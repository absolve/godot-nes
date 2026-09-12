namespace GodotNes.Core;

/// <summary>
/// Mapper 7 / AxROM（AMROM / ANROM / AOROM）：32KB 大块换 PRG + **单屏镜像**。
/// 《打气球》《忍者蛙》《Marble Madness》用它。
///
/// AOROM 板子上根本没有 CHR ROM，图案全在 8KB CHR RAM 里，所以换 PRG 就能换整套美术。
/// 单屏镜像意味着 4 块名称表里只有 $2000 那块是真的（bit4 选 $2000 还是 $2400），
/// 这类游戏常用它做"整屏滚动"：换了名称表整屏内容就翻篇。
/// </summary>
public sealed class AxromMapper : Mapper
{
	private const int PrgBank32K = 0x8000;

	private int _bank;
	private bool _upperScreen;

	public AxromMapper(Cartridge cartridge)
		: base(cartridge)
	{
	}

	public override string Name => "AxROM";

	public override Mirroring? DynamicMirroring =>
		_upperScreen ? Mirroring.SingleScreenUpper : Mirroring.SingleScreenLower;

	public override byte CpuRead(ushort address) =>
		Cartridge.PrgRom[PrgBank(_bank, PrgBank32K) + (address - 0x8000)];

	/// <summary>
	/// 写 $8000-$FFFF：bit0-3 选 32KB bank（最多 512KB），bit4 选单屏镜像的哪一块。
	/// 注意 AOROM 是"数据和 bank 一起写"，所以换屏和换 bank 是同一条指令完成的。
	/// </summary>
	public override void CpuWrite(ushort address, byte value)
	{
		_bank = value & 0x0F;
		_upperScreen = (value & 0x10) != 0;
	}

	public override byte PpuRead(ushort address) => ReadChr(address & 0x1FFF);

	public override void PpuWrite(ushort address, byte value) => WriteChr(address & 0x1FFF, value);

	public override void Reset()
	{
		_bank = 0;
		_upperScreen = false;
	}
}
