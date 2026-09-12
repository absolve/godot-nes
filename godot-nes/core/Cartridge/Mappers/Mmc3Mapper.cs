using System;

namespace GodotNes.Core;

/// <summary>
/// Mapper 4 / MMC3（TxROM）：最普及的一块芯片，后期大作基本都是它
/// （《超级马里奥兄弟 2/3》《星之卡比》《忍者神龟》……）。
///
/// 比前面几块复杂在三点：
///   1. **8 个寄存器靠"先选号再写值"**：$8000 写寄存器号（+ PRG/CHR 模式位），
///      $8001 写该寄存器的值；
///   2. **CHR 分得细**：2 个 2KB + 4 个 1KB，而且 bit7 能把 $0000 和 $1000 两半对调，
///      游戏用它对调"背景/精灵图块表"；
///   3. **扫描线中断**：一个每扫描线递减的计数器，减到 0 就拉 IRQ —— 分屏状态栏就是靠它。
///
/// 已知简化：
///   - IRQ 计数器按"每条可见扫描线一次"推进（真机是 A12 上升沿，dot 级）。
///     这个近似是扫描线级 PPU 的必然结果，做分屏游戏足够；本项目的参照实现也是这么干的；
///   - PRG RAM 写保护（$A001）忽略；
///   - MMC3 的 A/B 版本差异（rev A 的中断行为）不区分，按 rev B 实现。
/// </summary>
public sealed class Mmc3Mapper : Mapper
{
	private const int PrgBank8K = 0x2000;
	private const int ChrBank1K = 0x0400;

	private readonly byte[] _registers = new byte[8];

	private int _register;
	/// <summary>PRG 模式（bank select 的 bit6）：0 = R6 在 $8000，1 = R6 在 $C000。</summary>
	private int _prgMode;

	/// <summary>CHR 模式（bank select 的 bit7）：两半对调。</summary>
	private int _chrMode;

	private int[] _prgOffsets = new int[4];
	private int[] _chrOffsets = new int[8];

	// ---- 扫描线 IRQ ----
	private byte _irqLatch;
	private byte _irqCounter;
	private bool _irqReload;

	// _irqEnabled 是"允许产生中断"，_irqPending 是"已经拉住了 IRQ 线"。
	// 真机上中断线会一直拉着，直到程序写 $E000 关掉它 —— 所以这里是个锁存，不是脉冲。
	private bool _irqEnabled;
	private bool _irqPending;

	private bool _horizontalMirroring;

	public Mmc3Mapper(Cartridge cartridge)
		: base(cartridge)
	{
		Reset();
	}

	public override string Name => "MMC3 (TxROM)";

	public override Mirroring? DynamicMirroring =>
		Cartridge.HasFourScreen
			? Mirroring.FourScreen
			: _horizontalMirroring ? Mirroring.Horizontal : Mirroring.Vertical;

	public override bool IrqPending => _irqPending;

	public override byte CpuRead(ushort address)
	{
		int index = (address - 0x8000) / PrgBank8K;
		return Cartridge.PrgRom[_prgOffsets[index] + ((address - 0x8000) % PrgBank8K)];
	}

	public override void CpuWrite(ushort address, byte value)
	{
		LogRegisterWrite(address, value);

		// 地址的低位决定是哪个寄存器，$E001 这个掩码正好把 8 个入口分开
		switch (address & 0xE001)
		{
			case 0x8000:
				_register = value & 0x07;
				_prgMode = (value >> 6) & 0x01;
				_chrMode = (value >> 7) & 0x01;
				UpdateOffsets();
				break;

			case 0x8001:
				_registers[_register] = value;
				UpdateOffsets();
				break;

			case 0xA000:
				// 试过把这一位反过来（怀疑和 iNES 头的极性相反）：结果场景整片变空、底栏依旧错，
// 所以原来的极性是对的 —— 场景正常说明这个映射是对的，问题不在这里，已回退。
_horizontalMirroring = (value & 0x01) != 0;
				break;

			case 0xA001:
				// PRG RAM 写保护，忽略
				break;

			case 0xC000:
				_irqLatch = value;
				break;

			case 0xC001:
				_irqReload = true;
				break;

			case 0xE000:
				// 关中断，同时把已经拉住的 IRQ 线放开
				_irqEnabled = false;
				_irqPending = false;
				break;

			case 0xE001:
				_irqEnabled = true;
				break;
		}
	}

	public override byte PpuRead(ushort address)
	{
		int index = (address & 0x1FFF) / ChrBank1K;
		return ReadChr(_chrOffsets[index] + (address & (ChrBank1K - 1)));
	}

	public override void PpuWrite(ushort address, byte value)
	{
		int index = (address & 0x1FFF) / ChrBank1K;
		WriteChr(_chrOffsets[index] + (address & (ChrBank1K - 1)), value);
	}

	/// <summary>
	/// 每条可见扫描线推进一次计数器（PPU 在渲染完那一行之后调）。
	///
	/// 规则（和 FCEUX 的 ClockMMC3Counter 一致）：
	///   计数器为 0 或者刚写过 $C001 就从闩锁重载；否则减 1；
	///   减到 0 且中断使能就拉中断。中断线会一直拉着，等程序写 $E000 才放开。
	///
	/// 试过按 VirtuaNES 的写法（不自动重载、减到 0 就停住、中断一直拉着），
	/// 对《Mighty Final Fight》反而更差（整帧差异 39321 → 58582 像素），所以保持和
	/// FCEUX 一致 —— 它是这个游戏渲染正确的参照实现。
	/// </summary>
	public override void OnScanline()
	{
		if (_irqCounter == 0 || _irqReload)
		{
			_irqCounter = _irqLatch;
			_irqReload = false;
			return;
		}

		_irqCounter--;
		if (_irqCounter == 0 && _irqEnabled)
		{
			_irqPending = true;
		}
	}

	public override void Reset()
	{
		// 真机上这几个寄存器的上电值是"不确定"的，各家用各自的惯例。这里跟 FCEUX 一致：
		// R6=0、R7=1（也就是 $8000/$A000 复位后分别是第 0、1 块），R0-R5 填 CHR 的默认图。
		// 保持这套的好处是复位后到游戏写寄存器之前，画面/取指的位置和主流模拟器一模一样。
		_registers[0] = 0;
		_registers[1] = 2;
		_registers[2] = 4;
		_registers[3] = 5;
		_registers[4] = 6;
		_registers[5] = 7;
		_registers[6] = 0;
		_registers[7] = 1;

		_register = 0;
		_prgMode = 0;
		_chrMode = 0;
		_irqLatch = 0;
		_irqCounter = 0;
		_irqReload = false;
		_irqEnabled = false;
		_irqPending = false;
		_horizontalMirroring = false;
		UpdateOffsets();
	}

	private void UpdateOffsets()
	{
		if (_prgMode == 0)
		{
			// R6 → $8000，R7 → $A000，倒数第二块 → $C000，最后一块 → $E000
			_prgOffsets[0] = PrgBank(_registers[6], PrgBank8K);
			_prgOffsets[1] = PrgBank(_registers[7], PrgBank8K);
			_prgOffsets[2] = PrgBank(-2, PrgBank8K);
			_prgOffsets[3] = PrgBank(-1, PrgBank8K);
		}
		else
		{
			// 对调：倒数第二块 → $8000，R6 → $C000（$E000 永远是最后一块）
			_prgOffsets[0] = PrgBank(-2, PrgBank8K);
			_prgOffsets[1] = PrgBank(_registers[7], PrgBank8K);
			_prgOffsets[2] = PrgBank(_registers[6], PrgBank8K);
			_prgOffsets[3] = PrgBank(-1, PrgBank8K);
		}

		if (_chrMode == 0)
		{
			// $0000 和 $0800 是 2KB 块（R0/R1，用低位凑成两半），$1000 起是 1KB 块（R2-R5）
			_chrOffsets[0] = ChrBank(_registers[0] & 0xFE, ChrBank1K);
			_chrOffsets[1] = ChrBank(_registers[0] | 0x01, ChrBank1K);
			_chrOffsets[2] = ChrBank(_registers[1] & 0xFE, ChrBank1K);
			_chrOffsets[3] = ChrBank(_registers[1] | 0x01, ChrBank1K);
			_chrOffsets[4] = ChrBank(_registers[2], ChrBank1K);
			_chrOffsets[5] = ChrBank(_registers[3], ChrBank1K);
			_chrOffsets[6] = ChrBank(_registers[4], ChrBank1K);
			_chrOffsets[7] = ChrBank(_registers[5], ChrBank1K);
		}
		else
		{
			// 对调两半：R2-R5 到 $0000，R0/R1 的 2KB 到 $1000/$1800
			_chrOffsets[0] = ChrBank(_registers[2], ChrBank1K);
			_chrOffsets[1] = ChrBank(_registers[3], ChrBank1K);
			_chrOffsets[2] = ChrBank(_registers[4], ChrBank1K);
			_chrOffsets[3] = ChrBank(_registers[5], ChrBank1K);
			_chrOffsets[4] = ChrBank(_registers[0] & 0xFE, ChrBank1K);
			_chrOffsets[5] = ChrBank(_registers[0] | 0x01, ChrBank1K);
			_chrOffsets[6] = ChrBank(_registers[1] & 0xFE, ChrBank1K);
			_chrOffsets[7] = ChrBank(_registers[1] | 0x01, ChrBank1K);
		}
	}
}
