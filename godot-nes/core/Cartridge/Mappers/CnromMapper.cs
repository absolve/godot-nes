namespace GodotNes.Core;

/// <summary>
/// Mapper 3 / CNROM：只换 CHR 的廉价方案。PRG 固定（16KB 镜像或 32KB），
/// CHR 是 8KB 一块，写 $8000-$FFFF 选块，用低 2 位。
/// 《高桥名人的冒险岛》《爆破彗星》这类早期游戏用它。
///
/// 已知简化：真 CNROM 板有**总线冲突** —— 写的时候数据总线上是"要写的值 AND ROM 里那一个字节"，
/// 所以游戏必须小心地往值是 $FF 的地址写。不做这个模拟（绝大多数游戏的行为不受影响）。
/// </summary>
public sealed class CnromMapper : Mapper
{
	private const int PrgBank16K = 0x4000;
	private const int ChrBank8K = 0x2000;

	private int _chrBank;

	public CnromMapper(Cartridge cartridge)
		: base(cartridge)
	{
	}

	public override string Name => "CNROM";

	public override byte CpuRead(ushort address)
	{
		int bank = Cartridge.PrgSize <= PrgBank16K ? 0 : (address - 0x8000) / PrgBank16K;
		return Cartridge.PrgRom[PrgBank(bank, PrgBank16K) + ((address - 0x8000) % PrgBank16K)];
	}

	public override void CpuWrite(ushort address, byte value) => _chrBank = value & 0x03;

	public override byte PpuRead(ushort address) => ReadChr(ChrBank(_chrBank, ChrBank8K) + (address & 0x1FFF));

	public override void PpuWrite(ushort address, byte value) =>
		WriteChr(ChrBank(_chrBank, ChrBank8K) + (address & 0x1FFF), value);

	public override void Reset() => _chrBank = 0;
}
