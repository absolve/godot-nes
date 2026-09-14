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

	/// <summary>Four Score 的第三、第四个手柄（插上扩展器才有意义）。</summary>
	public Controller Controller3 { get; } = new();

	public Controller Controller4 { get; } = new();

	/// <summary>
	/// 是否插了 Four Score（四人分插器）。
	///
	/// 插上之后两个口各复用一组：$4016 先出 1P 再出 3P、$4017 先出 2P 再出 4P，
	/// 数据都在 bit0；bit1 在前 8 位是 0、第 8-23 位是 1（游戏靠它识别扩展器）。
	/// </summary>
	public bool FourScore { get; set; }

	/// <summary>Four Score 下每个口读到第几位了（写 $4016 的选通会清零）。</summary>
	private readonly int[] _fourScoreBit = new int[2];

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
				return FourScore ? ReadFourScore(0) : Controller1.Read();
			case 0x4017:
				return FourScore ? ReadFourScore(1) : Controller2.Read();
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

				if ((value & 0x01) != 0)
				{
					_fourScoreBit[0] = 0;      // 选通：Four Score 的读数位置一起归零
					_fourScoreBit[1] = 0;
				}

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

	/// <summary>
	/// Four Score 的一个口的读操作。协议（参照 nesdev + FCEUX 的 ReadGP）：
	///
	///   bit0 = 当前那一段的手柄数据位（0-7 位是 1P/2P，8-15 位是 3P/4P，16 位之后为 0）
	///   bit1 = 前 8 位是 0、第 8-23 位是 1 —— 游戏就是靠这一位判断"插了四人分插器"
	///   第 19 位（口 1）/第 18 位（口 2）额外把 bit0 置 1，兼容按 FCEUX 方式识别的游戏
	/// </summary>
	private byte ReadFourScore(int port)
	{
		int bit = _fourScoreBit[port]++;
		Controller pad = bit switch
		{
			< 8 => port == 0 ? Controller1 : Controller2,
			< 16 => port == 0 ? Controller3 : Controller4,
			_ => null!,
		};

		if (pad is null)
		{
			return 0;                              // 16 位之后没有数据了
		}

		byte result = (byte)((pad.Buttons >> (bit & 7)) & 0x01);
		if (bit >= 8)
		{
			result |= 0x02;                        // 第二组：bit1 = 1
		}

		if (bit == 19 - port)
		{
			result |= 0x01;                        // 识别位
		}

		return result;
	}
}
