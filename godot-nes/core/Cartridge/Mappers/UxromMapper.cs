namespace GodotNes.Core;

/// <summary>
/// Mapper 2 / UxROM（UNROM / UOROM）：最常用的"换 PRG 不换 CHR"方案。
/// 《魂斗罗》《洛克人》《恶魔城》早期作都是它。
///
/// 规则很简单：$8000-$BFFF 是可换的 16KB bank（写 $8000-$FFFF 的任意地址就切换），
/// $C000-$FFFF 永远是最后一块 —— 复位向量在那里，所以程序怎么换 bank 都不会跑飞。
/// CHR 固定 8KB（UNROM 板上是 CHR RAM，UOROM 是 CHR ROM）。
/// </summary>
public sealed class UxromMapper : Mapper
{
	private const int PrgBank16K = 0x4000;

	private int _bank;

	public UxromMapper(Cartridge cartridge)
		: base(cartridge)
	{
	}

	public override string Name => "UxROM";

	public override byte CpuRead(ushort address) =>
		address < 0xC000
			? Cartridge.PrgRom[PrgBank(_bank, PrgBank16K) + (address - 0x8000)]
			: Cartridge.PrgRom[PrgBank(-1, PrgBank16K) + (address - 0xC000)];

	/// <summary>写 $8000-$FFFF：低 4 位（有些板 5 位）选 bank，多出来的位由 bank 数取模吃掉。</summary>
	public override void CpuWrite(ushort address, byte value) => _bank = value;

	public override byte PpuRead(ushort address) => ReadChr(address & 0x1FFF);

	public override void PpuWrite(ushort address, byte value) => WriteChr(address & 0x1FFF, value);

	public override void Reset() => _bank = 0;
}
