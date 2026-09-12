using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GodotNes.Core;

/// <summary>名称表镜像方式，由卡带决定。</summary>
public enum Mirroring
{
	Horizontal,
	Vertical,
	FourScreen,
	SingleScreenLower,
	SingleScreenUpper,
}

/// <summary>CPU/PPU 时序制式（NES 2.0 的 byte 12 低 2 位）。</summary>
public enum TvSystem
{
	Ntsc,
	Pal,
	MultiRegion,
	Dendy,
}

/// <summary>运行平台（byte 7 低 2 位；为 Extended 时再看 byte 13 低半字节）。</summary>
public enum ConsoleType
{
	NesFamicom,
	VsSystem,
	PlayChoice10,
	Extended,
}

/// <summary>ROM 无法解析（缺 iNES 魔数、文件截断、PRG 为空等）时抛出。</summary>
public sealed class CartridgeFormatException : Exception
{
	public CartridgeFormatException(string message)
		: base(message)
	{
	}
}

/// <summary>
/// iNES 1.0 / NES 2.0 卡带。
///
/// 负责把 .nes 文件拆成四块：16 字节头、可选的 512 字节 trainer、PRG ROM（程序块）、
/// CHR ROM（精灵/图案块），并把头部里的"游戏相关信息"全部解析出来
/// （mapper、子 mapper、镜像、RAM 大小、制式、平台、电池、CRC32/MD5、复位向量…）。
///
/// 解析规则对齐了工作目录里的三个开源实现：
///   FCEUX      src/ines.cpp     —— 最权威，容错规则都在这里
///   fogleman/nes nes/ines.go    —— 结构清晰
///   jsnes      src/rom.js       —— NES 2.0 字段注释最完整
/// 关键结论（三份实现一致）：NES 2.0 的 mapper 高 4 位来自 **byte 8 的低半字节**
/// （byte 6 的低半字节仍然是镜像/电池/trainer/四屏标志），子 mapper 是 byte 8 的高半字节。
/// </summary>
public sealed class Cartridge
{
	public const int HeaderSize = 16;
	public const int TrainerSize = 512;
	public const int PrgBankSize = 0x4000;   // 16 KB
	public const int ChrBankSize = 0x2000;   // 8 KB

	private Cartridge()
	{
		Header = new byte[HeaderSize];
	}

	// ---------------------------------------------------------------- 文件块

	/// <summary>原始 16 字节头（已做过垃圾清理）。</summary>
	public byte[] Header { get; private set; }

	/// <summary>PRG ROM —— 程序块，映射到 CPU $8000-$FFFF。</summary>
	public byte[] PrgRom { get; private set; } = Array.Empty<byte>();

	/// <summary>CHR ROM —— 图案/精灵块，映射到 PPU $0000-$1FFF。CHR RAM 时为空数组。</summary>
	public byte[] ChrRom { get; private set; } = Array.Empty<byte>();

	/// <summary>512 字节 trainer（$7000-$71FF），没有则为空数组。</summary>
	public byte[] Trainer { get; private set; } = Array.Empty<byte>();

	/// <summary>CHR RAM（CHR ROM 大小为 0 时使用），映射在 PPU $0000-$1FFF。</summary>
	public byte[] ChrRam { get; private set; } = Array.Empty<byte>();

	// 说明：卡带 WRAM 的实际存储放在 Bus 里（$6000-$7FFF）。这里只保留"头里声明了多大"
	// 这两个数字用于显示；等做到 MMC5 这种需要大 WRAM 的 mapper 时，再把存储挪过来。

	// ---------------------------------------------------------------- 头部信息

	public bool IsNes20 { get; private set; }

	public int MapperNumber { get; private set; }

	/// <summary>NES 2.0 才有意义的子 mapper（iNES 1.0 恒为 0）。</summary>
	public int SubmapperNumber { get; private set; }

	/// <summary>mapper 家族名（NROM / MMC1 / UxROM …），只用于显示，不代表已实现。</summary>
	public string MapperName { get; private set; } = "未知";

	public Mirroring Mirroring { get; private set; } = Mirroring.Horizontal;

	public TvSystem TvSystem { get; private set; } = TvSystem.Ntsc;

	public ConsoleType ConsoleType { get; private set; } = ConsoleType.NesFamicom;

	/// <summary>ConsoleType == Extended 时，byte 13 的低半字节。</summary>
	public int ExtendedConsoleType { get; private set; }

	public bool HasBattery { get; private set; }

	public bool HasTrainer => Trainer.Length > 0;

	public bool HasFourScreen => Mirroring == Mirroring.FourScreen;

	/// <summary>PRG RAM 大小（字节）。</summary>
	public int PrgRamSize { get; private set; }

	/// <summary>带电池的 PRG NVRAM 大小（字节）。</summary>
	public int PrgNvRamSize { get; private set; }

	/// <summary>CHR RAM 大小（字节）。</summary>
	public int ChrRamSize { get; private set; }

	/// <summary>带电池的 CHR NVRAM 大小（字节）。</summary>
	public int ChrNvRamSize { get; private set; }

	/// <summary>解析过程中遇到的、不致命但值得提醒的问题。</summary>
	public IReadOnlyList<string> Warnings => _warnings;

	private readonly List<string> _warnings = new();

	/// <summary>ROM 文件路径（可能为空，例如直接从内存解析）。</summary>
	public string SourcePath { get; private set; } = string.Empty;

	/// <summary>文件总字节数（含头、trainer）。</summary>
	public long FileSize { get; private set; }

	// ---------------------------------------------------------------- 派生信息

	public int PrgSize => PrgRom.Length;

	public int ChrSize => ChrRom.Length;

	public int PrgBankCount => PrgRom.Length / PrgBankSize;

	public int ChrBankCount => ChrRom.Length / ChrBankSize;

	/// <summary>CHR 是卡带上的 ROM 还是主机上的 RAM。</summary>
	public bool UsesChrRam => ChrRom.Length == 0;

	/// <summary>复位向量（由最后一块 PRG 的 $FFFC/$FFFD 得到）。</summary>
	public ushort ResetVector { get; private set; }

	/// <summary>NMI 向量（$FFFA/$FFFB）。</summary>
	public ushort NmiVector { get; private set; }

	/// <summary>IRQ/BRK 向量（$FFFE/$FFFF）。</summary>
	public ushort IrqVector { get; private set; }

	/// <summary>PRG + CHR 的 CRC32（和 FCEUX 一样，用来对照 ROM 数据库）。</summary>
	public uint Crc32 { get; private set; }

	/// <summary>PRG + CHR 的 MD5，十六进制小写。</summary>
	public string Md5 { get; private set; } = string.Empty;

	/// <summary>映射器实例，构造卡带时创建。</summary>
	public Mapper Mapper { get; private set; } = null!;

	// ---------------------------------------------------------------- 解析

	/// <summary>从磁盘读取 .nes 文件。</summary>
	public static Cartridge FromFile(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new ArgumentException("ROM 路径为空。", nameof(path));
		}

		if (!File.Exists(path))
		{
			throw new CartridgeFormatException($"找不到 ROM 文件：{path}");
		}

		byte[] data = File.ReadAllBytes(path);
		return FromBytes(data, path);
	}

	/// <summary>不抛异常的版本，失败时返回 false 并给出原因（给 UI 用）。</summary>
	public static bool TryFromFile(string path, out Cartridge? cartridge, out string error)
	{
		try
		{
			cartridge = FromFile(path);
			error = string.Empty;
			return true;
		}
		catch (Exception ex) when (ex is CartridgeFormatException or IOException
									   or UnauthorizedAccessException or ArgumentException
									   or NotSupportedException)
		{
			cartridge = null;
			error = ex.Message;
			return false;
		}
	}

	/// <summary>解析内存里的 .nes 数据。</summary>
	public static Cartridge FromBytes(byte[] data, string sourcePath = "")
	{
		if (data is null)
		{
			throw new ArgumentNullException(nameof(data));
		}

		if (data.Length < HeaderSize)
		{
			throw new CartridgeFormatException($"文件只有 {data.Length} 字节，连 16 字节头都不够。");
		}

		if (data[0] != (byte)'N' || data[1] != (byte)'E' || data[2] != (byte)'S' || data[3] != 0x1A)
		{
			throw new CartridgeFormatException("找不到 iNES 魔数 \"NES\\x1A\"，可能不是 NES ROM。");
		}

		var cartridge = new Cartridge
		{
			SourcePath = sourcePath ?? string.Empty,
			FileSize = data.Length,
		};

		// 先拷贝一份头再做垃圾清理：ROM 头里常被老工具塞进 "DiskDude!" 之类的字符串，
		// 它们会污染 byte 8-15，导致 mapper 号算错。规则照抄 FCEUX 的 iNES_HEADER::cleanup()。
		Array.Copy(data, cartridge.Header, HeaderSize);
		cartridge.CleanHeaderGarbage();
		cartridge.ParseHeaderFields();

		// ---- 定位 PRG / CHR ----
		int offset = HeaderSize;
		if ((cartridge.Header[6] & 0x04) != 0)
		{
			if (data.Length < offset + TrainerSize)
			{
				throw new CartridgeFormatException("头里声明有 512 字节 trainer，但文件没那么长。");
			}

			cartridge.Trainer = new byte[TrainerSize];
			Array.Copy(data, offset, cartridge.Trainer, 0, TrainerSize);
			offset += TrainerSize;
		}

		int chrSize = cartridge.DeclaredChrSize;
		int prgSize = cartridge.DeclaredPrgSize;

		int remaining = data.Length - offset;
		if (prgSize == 0)
		{
			// 极少见的坏头：byte 4 = 0。FCEUX 的处理是"当成 4 MB 读"，这里改成按文件实际长度推断，
			// 更稳而且不会越界。
			int guess = (remaining - Math.Max(chrSize, 0)) / PrgBankSize * PrgBankSize;
			if (guess <= 0)
			{
				throw new CartridgeFormatException("PRG ROM 大小为 0（byte 4 = 0），且无法从文件长度推断。");
			}

			prgSize = guess;
			cartridge._warnings.Add($"头里 PRG 大小为 0，按文件长度推断为 {prgSize / 1024} KB。");
		}

		if (remaining < (long)prgSize + Math.Max(chrSize, 0))
		{
			throw new CartridgeFormatException(
				$"文件被截断：头里声明 PRG {prgSize} + CHR {chrSize} 字节，" +
				$"实际只有 {remaining} 字节（缺 {prgSize + Math.Max(chrSize, 0) - remaining} 字节）。");
		}

		cartridge.PrgRom = new byte[prgSize];
		Array.Copy(data, offset, cartridge.PrgRom, 0, prgSize);

		if (chrSize > 0)
		{
			cartridge.ChrRom = new byte[chrSize];
			Array.Copy(data, offset + prgSize, cartridge.ChrRom, 0, chrSize);
		}

		cartridge.FinishSetup();
		return cartridge;
	}

	/// <summary>PRG 大小（字节），NES 2.0 时含高位与指数形式。</summary>
	private int DeclaredPrgSize { get; set; }

	private int DeclaredChrSize { get; set; }

	/// <summary>
	/// 清掉老工具塞进头部的垃圾字符串。规则来自 FCEUX 的 iNES_HEADER::cleanup()。
	/// </summary>
	private void CleanHeaderGarbage()
	{
		byte[] h = Header;

		if (Matches(h, 0x07, "DiskDude") || Matches(h, 0x07, "demiforce"))
		{
			Array.Clear(h, 0x07, 0x09);
			_warnings.Add("头部 byte 7-15 里有老的写头工具留下的垃圾，已清零（mapper 号按 iNES 1.0 计算）。");
		}

		if (Matches(h, 0x0A, "Ni03"))
		{
			if (Matches(h, 0x07, "Dis"))
			{
				Array.Clear(h, 0x07, 0x09);
			}
			else
			{
				Array.Clear(h, 0x0A, 0x06);
			}

			_warnings.Add("头部里有 \"Ni03\" 写头工具留下的垃圾，已清零。");
		}
	}

	private static bool Matches(byte[] header, int offset, string ascii)
	{
		if (offset + ascii.Length > header.Length)
		{
			return false;
		}

		for (int i = 0; i < ascii.Length; i++)
		{
			if (header[offset + i] != (byte)ascii[i])
			{
				return false;
			}
		}

		return true;
	}

	private void ParseHeaderFields()
	{
		byte[] h = Header;

		byte flags6 = h[6];
		byte flags7 = h[7];
		byte flags8 = h[8];

		// iNES 2.0 的判定：byte 7 的 bit3..2 == 0b10（FCEUX / jsnes 都是这么判的）。
		IsNes20 = (flags7 & 0x0C) == 0x08;

		MapperNumber = (flags6 >> 4) | (flags7 & 0xF0);
		if (IsNes20)
		{
			// NES 2.0 把 mapper 扩到 12 位：byte 8 的低半字节是 bit8-11，
			// 高半字节是 submapper。注意 byte 6 的低半字节仍然是镜像/电池标志。
			MapperNumber |= (flags8 & 0x0F) << 8;
			SubmapperNumber = (flags8 >> 4) & 0x0F;
		}

		Mirroring = (flags6 & 0x08) != 0
			? Mirroring.FourScreen
			: (flags6 & 0x01) != 0 ? Mirroring.Vertical : Mirroring.Horizontal;

		HasBattery = (flags6 & 0x02) != 0;

		ConsoleType = (ConsoleType)(flags7 & 0x03);
		if (ConsoleType == ConsoleType.Extended)
		{
			ExtendedConsoleType = h[13] & 0x0F;
		}

		if (IsNes20)
		{
			// byte 9：PRG 大小的高 4 位 + CHR 大小的高 4 位。
			int prgHigh = h[9] & 0x0F;
			int chrHigh = (h[9] >> 4) & 0x0F;
			DeclaredPrgSize = DecodeRomSize(prgHigh, h[4], PrgBankSize);
			DeclaredChrSize = DecodeRomSize(chrHigh, h[5], ChrBankSize);

			// byte 10/11：RAM 各半字节，0 = 没有，否则 64 << n 字节。
			PrgRamSize = DecodeRamSize(h[10] & 0x0F);
			PrgNvRamSize = DecodeRamSize((h[10] >> 4) & 0x0F);
			ChrRamSize = DecodeRamSize(h[11] & 0x0F);
			ChrNvRamSize = DecodeRamSize((h[11] >> 4) & 0x0F);

			TvSystem = (TvSystem)(h[12] & 0x03);
		}
		else
		{
			DeclaredPrgSize = h[4] * PrgBankSize;
			DeclaredChrSize = h[5] * ChrBankSize;

			// iNES 1.0 没有 RAM 大小字段，按惯例给 8 KB WRAM。
			PrgRamSize = 0x2000;
			TvSystem = TvSystem.Ntsc;

			if (HasNonZeroTail(h))
			{
				_warnings.Add("iNES 1.0 头的 byte 8-15 非全零（老工具留下的垃圾？），" +
							 "如果 mapper 号看着不对，可以怀疑这里。");
			}
		}

		MapperName = MapperNames.GetName(MapperNumber);
	}

	/// <summary>
	/// 解 ROM 大小：正常情况下是「高 4 位 &lt;&lt; 8 | 低字节」个 bank；
	/// 高半字节为 0xF 时切成指数形式：size = 2^(低字节&gt;&gt;2) * ((低字节&amp;3)*2+1) 字节。
	/// </summary>
	private static int DecodeRomSize(int highNibble, byte lowByte, int bankSize)
	{
		if (highNibble != 0x0F)
		{
			return ((highNibble << 8) | lowByte) * bankSize;
		}

		long size = (1L << (lowByte >> 2)) * (((lowByte & 0x03) * 2) + 1);
		if (size > int.MaxValue)
		{
			throw new CartridgeFormatException($"NES 2.0 指数形式的 ROM 大小过大（{size} 字节）。");
		}

		return (int)size;
	}

	/// <summary>NES 2.0 的 RAM 大小：0 表示没有，否则 64 &lt;&lt; n 字节。</summary>
	private static int DecodeRamSize(int nibble) => nibble == 0 ? 0 : 64 << nibble;

	private static bool HasNonZeroTail(byte[] header)
	{
		for (int i = 8; i < HeaderSize; i++)
		{
			if (header[i] != 0)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>分配 RAM、算校验和与向量、建映射器。</summary>
	private void FinishSetup()
	{
		if (UsesChrRam)
		{
			// iNES 1.0 没有 CHR RAM 大小字段，惯例是 8 KB。
			// NES 2.0 理论上要把大小写在 byte 11，但真实 ROM 里经常留 0，
			// 这里为了能跑起来还是给 8 KB，同时提示一句。
			int size = ChrRamSize > 0 ? ChrRamSize : ChrBankSize;
			if (IsNes20 && ChrRamSize == 0)
			{
				_warnings.Add("NES 2.0 头里没写 CHR RAM 大小，按 8 KB 处理（否则没有图案数据可用）。");
			}

			ChrRamSize = size;
			ChrRam = new byte[size];
		}

		ReadVectors();
		ComputeChecksums();

		Mapper = MapperFactory.Create(this);
	}

	private void ReadVectors()
	{
		// 上电时最后一块 16 KB PRG 总是映射在 $C000-$FFFF，向量表在它的最后 6 个字节。
		int length = PrgRom.Length;
		if (length < 6)
		{
			return;
		}

		NmiVector = (ushort)(PrgRom[length - 6] | (PrgRom[length - 5] << 8));
		ResetVector = (ushort)(PrgRom[length - 4] | (PrgRom[length - 3] << 8));
		IrqVector = (ushort)(PrgRom[length - 2] | (PrgRom[length - 1] << 8));
	}

	private void ComputeChecksums()
	{
		Crc32 = Crc32Of(PrgRom, ChrRom);

		using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
		md5.AppendData(PrgRom);
		md5.AppendData(ChrRom);
		Md5 = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();
	}

	/// <summary>CRC32（IEEE 802.3，和 FCEUX 报的是同一种，方便对照 ROM 数据库）。</summary>
	private static uint Crc32Of(byte[] prg, byte[] chr)
	{
		uint crc = 0xFFFFFFFF;
		crc = Crc32Update(crc, prg);
		crc = Crc32Update(crc, chr);
		return crc ^ 0xFFFFFFFF;
	}

	private static readonly uint[] Crc32Table = BuildCrc32Table();

	private static uint[] BuildCrc32Table()
	{
		var table = new uint[256];
		for (uint i = 0; i < 256; i++)
		{
			uint value = i;
			for (int bit = 0; bit < 8; bit++)
			{
				value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
			}

			table[i] = value;
		}

		return table;
	}

	private static uint Crc32Update(uint crc, byte[] data)
	{
		foreach (byte b in data)
		{
			crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
		}

		return crc;
	}

	// ---------------------------------------------------------------- 显示

	/// <summary>一行 ROM 概要，给状态栏和日志用。</summary>
	public string Describe()
	{
		var sb = new StringBuilder();
		sb.Append("Mapper ").Append(MapperNumber);
		sb.Append(" (").Append(MapperName).Append(')');
		sb.Append(", PRG ").Append(PrgSize / 1024).Append("KB");
		sb.Append(", CHR ");
		if (UsesChrRam)
		{
			sb.Append("RAM ").Append(ChrRamSize / 1024).Append("KB");
		}
		else
		{
			sb.Append(ChrSize / 1024).Append("KB");
		}

		sb.Append(", ").Append(MirroringText);
		sb.Append(", ").Append(TvSystemText);
		return sb.ToString();
	}

	/// <summary>多行详情，给命令行 <c>--rom-info</c> 用（格式参考 FCEUX 的打印）。</summary>
	public string DescribeDetailed()
	{
		var sb = new StringBuilder();
		sb.Append("文件      : ").Append(SourcePath.Length > 0 ? SourcePath : "(内存)").Append('\n');
		sb.Append("大小      : ").Append(FileSize.ToString("N0", CultureInfo.InvariantCulture)).Append(" 字节");
		sb.Append("  (头 16 + ").Append(HasTrainer ? "trainer 512 + " : string.Empty);
		sb.Append("PRG ").Append(PrgSize).Append(" + CHR ").Append(ChrSize).Append(")\n");
		sb.Append("格式      : ").Append(IsNes20 ? "NES 2.0" : "iNES 1.0").Append('\n');
		sb.Append("Mapper    : ").Append(MapperNumber).Append(" (").Append(MapperName).Append(')');
		if (IsNes20)
		{
			sb.Append("  子 mapper ").Append(SubmapperNumber);
		}

		sb.Append('\n');
		sb.Append("PRG ROM   : ").Append(PrgSize / 1024).Append(" KB（").Append(PrgBankCount).Append(" x 16KB）");
		sb.Append("  复位向量 $").Append(ResetVector.ToString("X4", CultureInfo.InvariantCulture)).Append('\n');
		sb.Append("CHR ROM   : ");
		if (UsesChrRam)
		{
			sb.Append("无（用 ").Append(ChrRamSize / 1024).Append(" KB CHR RAM）");
		}
		else
		{
			sb.Append(ChrSize / 1024).Append(" KB（").Append(ChrBankCount).Append(" x 8KB）");
		}

		sb.Append('\n');
		sb.Append("WRAM      : ").Append(PrgRamSize / 1024).Append(" KB");
		if (PrgNvRamSize > 0)
		{
			sb.Append("（另有电池 NVRAM ").Append(PrgNvRamSize / 1024).Append(" KB）");
		}

		sb.Append('\n');
		sb.Append("CHR RAM   : ").Append(ChrRamSize / 1024).Append(" KB");
		if (ChrNvRamSize > 0)
		{
			sb.Append("（另有电池 CHR-NVRAM ").Append(ChrNvRamSize / 1024).Append(" KB）");
		}

		sb.Append('\n');
		sb.Append("镜像      : ").Append(MirroringText).Append('\n');
		sb.Append("制式      : ").Append(TvSystemText).Append('\n');
		sb.Append("平台      : ").Append(ConsoleTypeText);
		if (ConsoleType == ConsoleType.Extended)
		{
			sb.Append("（扩展类型 ").Append(ExtendedConsoleType).Append("）");
		}

		sb.Append('\n');
		sb.Append("电池存档  : ").Append(HasBattery ? "有" : "无").Append('\n');
		sb.Append("Trainer   : ").Append(HasTrainer ? "有" : "无").Append('\n');
		sb.Append("向量表    : NMI $").Append(NmiVector.ToString("X4", CultureInfo.InvariantCulture));
		sb.Append("  RESET $").Append(ResetVector.ToString("X4", CultureInfo.InvariantCulture));
		sb.Append("  IRQ $").Append(IrqVector.ToString("X4", CultureInfo.InvariantCulture)).Append('\n');
		sb.Append("CRC32     : ").Append(Crc32.ToString("X8", CultureInfo.InvariantCulture)).Append('\n');
		sb.Append("MD5       : ").Append(Md5).Append('\n');
		sb.Append("映射器    : ").Append(MapperName).Append('（').Append(Mapper.GetType().Name).Append("）\n");

		if (Warnings.Count > 0)
		{
			sb.Append("提示      :\n");
			foreach (string warning in Warnings)
			{
				sb.Append("  - ").Append(warning).Append('\n');
			}
		}

		return sb.ToString();
	}

	public string MirroringText => DescribeMirroring(Mirroring);

	/// <summary>
	/// 把镜像方式说成人话。卡带信息窗口显示卡带头里的值，PPU 窗口显示**当前生效**的值
	/// （MMC1 / MMC3 / AxROM 会随时改镜像，那个才是渲染真正用的）。
	/// </summary>
	public static string DescribeMirroring(Mirroring mirroring) => mirroring switch
	{
		Mirroring.Horizontal => "水平",
		Mirroring.Vertical => "垂直",
		Mirroring.FourScreen => "四屏",
		Mirroring.SingleScreenLower => "单屏(低)",
		_ => "单屏(高)",
	};

	public string TvSystemText => TvSystem switch
	{
		TvSystem.Ntsc => "NTSC",
		TvSystem.Pal => "PAL",
		TvSystem.MultiRegion => "多制式",
		_ => "Dendy",
	};

	public string ConsoleTypeText => ConsoleType switch
	{
		ConsoleType.NesFamicom => "NES / Famicom",
		ConsoleType.VsSystem => "Vs. System",
		ConsoleType.PlayChoice10 => "PlayChoice-10",
		_ => "扩展平台",
	};
}
