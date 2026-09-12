namespace GodotNes.Core;

/// <summary>
/// Mapper 0 / NROM：最基础的卡带。PRG 16KB 或 32KB，CHR ROM 8KB（或 CHR RAM）。
/// 《超级马里奥兄弟》用的就是这个。
///
/// 卡带上没有任何 bank 切换寄存器，所以对 $8000-$FFFF 的写是**无声的**
/// （真机上会打到 ROM 上，Nothing happens）。
/// </summary>
public sealed class NromMapper : Mapper
{
	private const int PrgBank16K = 0x4000;

	public NromMapper(Cartridge cartridge)
		: base(cartridge)
	{
	}

	public override string Name => "NROM";

	public override byte CpuRead(ushort address)
	{
		// 16KB PRG 时 $C000-$FFFF 是 $8000-$BFFF 的镜像；32KB 时两块各自独立
		int bank = Cartridge.PrgSize <= PrgBank16K ? 0 : (address - 0x8000) / PrgBank16K;
		return Cartridge.PrgRom[PrgBank(bank, PrgBank16K) + ((address - 0x8000) % PrgBank16K)];
	}

	public override byte PpuRead(ushort address) => ReadChr(address & 0x1FFF);

	public override void PpuWrite(ushort address, byte value) => WriteChr(address & 0x1FFF, value);
}
