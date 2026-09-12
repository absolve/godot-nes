using System;

namespace GodotNes.Core;

/// <summary>
/// CPU 内存映射：
/// $0000-$07FF 2KB RAM（镜像到 $1FFF）
/// $2000-$3FFF PPU 寄存器（每 8 字节镜像一次）
/// $4000-$4017 APU / OAM DMA / 手柄
/// $6000-$7FFF 卡带 WRAM
/// $8000-$FFFF 卡带 PRG（交给 Mapper）
/// </summary>
public sealed class Bus : IBus
{
	public const int RamSize = 0x800;
	public const int PrgRamSize = 0x2000;

	private readonly byte[] _ram = new byte[RamSize];
	private readonly byte[] _prgRam = new byte[PrgRamSize];

	public Bus(Ppu ppu, Apu apu)
	{
		Ppu = ppu ?? throw new ArgumentNullException(nameof(ppu));
		Apu = apu ?? throw new ArgumentNullException(nameof(apu));
	}

	public Ppu Ppu { get; }

	public Apu Apu { get; }

	public Cartridge? Cartridge { get; set; }

	public Controller Controller1 { get; } = new();

	public Controller Controller2 { get; } = new();

	/// <summary>见 <see cref="IBus.OamDmaStallPending"/>：写 $4014 时置位，由 CPU 取走。</summary>
	public bool OamDmaStallPending { get; set; }

	public byte[] Ram => _ram;

	public byte[] PrgRam => _prgRam;

	public byte Read(ushort address)
	{
		switch (address)
		{
			case < 0x2000:
				return _ram[address & 0x07FF];
			case < 0x4000:
				return Ppu.ReadRegister((ushort)(address & 0x0007));
			case 0x4014:
				return 0x00;   // OAM DMA 只写不读
			case 0x4015:
				return Apu.ReadStatus();
			case 0x4016:
				return Controller1.Read();
			case 0x4017:
				return Controller2.Read();
			case < 0x4020:
				return 0x00;   // $4018-$401F 是测试寄存器，未使用
			case < 0x6000:
				return 0x00;   // 扩展区，未使用
			case < 0x8000:
				return _prgRam[address - 0x6000];
			default:
				return Cartridge is null ? (byte)0x00 : Cartridge.Mapper.CpuRead(address);
		}
	}

	public void Write(ushort address, byte value)
	{
		switch (address)
		{
			case < 0x2000:
				_ram[address & 0x07FF] = value;
				break;
			case < 0x4000:
				Ppu.WriteRegister((ushort)(address & 0x0007), value);
				break;
			case 0x4014:
				PerformOamDma(value);
				break;
			case 0x4015:
				Apu.WriteStatus(value);
				break;
			case 0x4016:
				Controller1.Write(value);
				Controller2.Write(value);
				break;
			case 0x4017:
				Apu.WriteFrameCounter(value);
				break;
			case < 0x4020:
				Apu.WriteRegister(address, value);
				break;
			case < 0x6000:
				break;         // 扩展区，忽略
			case < 0x8000:
				_prgRam[address - 0x6000] = value;
				break;
			default:
				Cartridge?.Mapper.CpuWrite(address, value);
				break;
		}
	}

	/// <summary>读 16 位小端，用于取复位/NMI/IRQ 向量。</summary>
	public ushort Read16(ushort address)
	{
		byte low = Read(address);
		byte high = Read((ushort)(address + 1));
		return (ushort)(low | (high << 8));
	}

	/// <summary>
	/// $4014 OAM DMA：从 CPU 页 $XX00-$XXFF 拷 256 字节进 PPU 的 OAM。
	/// 真机上这段时间 CPU 会停 513/514 个周期，所以这里置一下停顿标志，
	/// 由 <see cref="Cpu.Step"/> 把周期数补上（PPU / APU 照样继续跑）。
	/// </summary>
	private void PerformOamDma(byte page)
	{
		ushort baseAddress = (ushort)(page << 8);
		Span<byte> pageData = stackalloc byte[256];
		for (int i = 0; i < 256; i++)
		{
			pageData[i] = Read((ushort)(baseAddress + i));
		}

		Ppu.WriteOamDma(pageData);
		OamDmaStallPending = true;
	}
}
