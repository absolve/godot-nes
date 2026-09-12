using System;

namespace GodotNes.Core;

/// <summary>
/// 卡带映射器基类。数据通路分成两条：
/// CPU 侧是 $8000-$FFFF，PPU 侧是 $0000-$1FFF（pattern table）。
///
/// 子类要覆写的东西分三类：
///   - 取数据：<see cref="CpuRead"/> / <see cref="PpuRead"/>（换 bank 就靠它）；
///   - 收寄存器写：<see cref="CpuWrite"/> / <see cref="PpuWrite"/>；
///   - 反过来影响机器：<see cref="DynamicMirroring"/>（改名表镜像）、
///     <see cref="OnScanline"/> + <see cref="IrqPending"/>（MMC3 的扫描线中断）。
/// </summary>
public abstract class Mapper
{
	protected Mapper(Cartridge cartridge)
	{
		Cartridge = cartridge ?? throw new ArgumentNullException(nameof(cartridge));
	}

	protected Cartridge Cartridge { get; }

	/// <summary>
	/// 寄存器写日志钩子（调试用，默认 null）。排查"分屏时 bank 换错"这类问题时，
	/// 把每次 CPU 写映射器寄存器的 (地址, 值) 记下来，和参照实现的日志逐条比 ——
	/// 比整机 trace 便宜得多，而且直接回答"是游戏写错了值，还是我解码错了"。
	/// </summary>
	public Action<ushort, byte>? RegisterWriteLogger { get; set; }

	/// <summary>子类在 CpuWrite 里调用它上报一次寄存器写。</summary>
	protected void LogRegisterWrite(ushort address, byte value) =>
		RegisterWriteLogger?.Invoke(address, value);

	/// <summary>映射器名字，只用于调试显示。</summary>
	public abstract string Name { get; }

	/// <summary>CPU 读 $8000-$FFFF。</summary>
	public abstract byte CpuRead(ushort address);

	/// <summary>CPU 写 $8000-$FFFF。多数 mapper 把它当 bank 切换寄存器。</summary>
	public virtual void CpuWrite(ushort address, byte value)
	{
	}

	/// <summary>PPU 读 $0000-$1FFF（pattern table）。</summary>
	public abstract byte PpuRead(ushort address);

	/// <summary>PPU 写 $0000-$1FFF，只有 CHR RAM 时才有效。</summary>
	public virtual void PpuWrite(ushort address, byte value)
	{
	}

	/// <summary>
	/// 映射器自己控制的名称表镜像（MMC1 / MMC3 / AxROM 会随时改）。
	/// null 表示"用卡带头里那个固定值"，PPU 会自己去取。
	/// </summary>
	public virtual Mirroring? DynamicMirroring => null;

	/// <summary>PPU 每渲染完一条可见扫描线调用一次（MMC3 的扫描线 IRQ 用）。</summary>
	public virtual void OnScanline()
	{
	}

	/// <summary>是否有挂起的 IRQ（MMC3 等 mapper）。</summary>
	public virtual bool IrqPending => false;

	public virtual void Reset()
	{
	}

	// ------------------------------------------------------------------ 给子类用的小工具

	/// <summary>PRG ROM 总字节数。</summary>
	protected int PrgSize => Cartridge.PrgRom.Length;

	/// <summary>CHR 的总字节数：有 CHR ROM 用 ROM，否则用 CHR RAM。</summary>
	protected int ChrSize => Cartridge.UsesChrRam ? Cartridge.ChrRam.Length : Cartridge.ChrRom.Length;

	/// <summary>
	/// 把 bank 号折成合法索引再乘 bank 大小。负数表示"从后往前数"（-1 = 最后一块），
	/// 超出范围的按 bank 数取模 —— 卡带上的 bank 线接不满时，真机就是这么绕回去的。
	/// </summary>
	protected static int BankOffset(int bank, int bankSize, int totalSize)
	{
		if (bankSize <= 0 || totalSize < bankSize)
		{
			return 0;
		}

		int count = totalSize / bankSize;
		int index = ((bank % count) + count) % count;
		return index * bankSize;
	}

	/// <summary>PRG 里第 <paramref name="bank"/> 块（16KB / 8KB / 32KB）的字节偏移。</summary>
	protected int PrgBank(int bank, int bankSize) => BankOffset(bank, bankSize, PrgSize);

	/// <summary>CHR 里第 <paramref name="bank"/> 块的字节偏移（1KB / 2KB / 4KB / 8KB）。</summary>
	protected int ChrBank(int bank, int bankSize) => BankOffset(bank, bankSize, ChrSize);

	/// <summary>读 CHR（ROM 或 RAM），offset 会自动折回合法范围。</summary>
	protected byte ReadChr(int offset)
	{
		if (Cartridge.UsesChrRam)
		{
			return Cartridge.ChrRam[Wrap(offset, Cartridge.ChrRam.Length)];
		}

		return Cartridge.ChrRom[Wrap(offset, Cartridge.ChrRom.Length)];
	}

	/// <summary>写 CHR：只有 CHR RAM 才写得进去，CHR ROM 上写无效。</summary>
	protected void WriteChr(int offset, byte value)
	{
		if (!Cartridge.UsesChrRam)
		{
			return;
		}

		Cartridge.ChrRam[Wrap(offset, Cartridge.ChrRam.Length)] = value;
	}

	private static int Wrap(int index, int size)
	{
		if (size <= 0)
		{
			return 0;
		}

		return ((index % size) + size) % size;
	}
}
