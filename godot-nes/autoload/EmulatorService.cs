using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;
using GodotNes.Core;

namespace GodotNes.Game;

/// <summary>
/// 全局单例（autoload）。持有唯一的 <see cref="NesConsole"/>，负责 ROM 装载、复位、暂停。
/// 场景节点只跟它打交道，不自己 new 模拟器，以后加存档 / 设置也有统一的落点。
/// </summary>
public partial class EmulatorService : Node
{
	[Signal]
	public delegate void RomLoadedEventHandler(string path, string description);

	[Signal]
	public delegate void RomLoadFailedEventHandler(string path, string error);

	[Signal]
	public delegate void RomClosedEventHandler();

	/// <summary>autoload 单例。</summary>
	public static EmulatorService? Instance { get; private set; }

	public NesConsole Console { get; } = new();

	public string CurrentRomPath { get; private set; } = string.Empty;

	public string CurrentRomDescription { get; private set; } = string.Empty;

	public string LastError { get; private set; } = string.Empty;

	public override void _EnterTree()
	{
		Instance = this;
	}

	/// <summary>
	/// 自己量一个"模拟器实际跑多快"（NES 帧/秒）。
	/// 为什么不在 <c>Engine.GetFramesPerSecond()</c> 上凑合：那个是**显示帧率**（165Hz 屏上就是 165），
	/// 和游戏速度是两回事 —— "感觉跑得快"这类问题，要把两个数摆在一起才看得出来。
	/// </summary>
	public override void _Process(double delta)
	{
		long frame = Console.FrameCount;
		if (_nesFpsLastFrame < 0)
		{
			_nesFpsLastFrame = frame;
			return;
		}

		_nesFpsSeconds += delta;
		_nesFpsFrames += frame - _nesFpsLastFrame;
		_nesFpsLastFrame = frame;

		if (_nesFpsSeconds >= 0.5)
		{
			NesFps = _nesFpsFrames / _nesFpsSeconds;
			_nesFpsSeconds = 0;
			_nesFpsFrames = 0;
		}
	}

	/// <summary>最近半秒量到的 NES 帧率（真机 NTSC 是 60.0988）。</summary>
	public double NesFps { get; private set; }

	private double _nesFpsSeconds;
	private long _nesFpsFrames;
	private long _nesFpsLastFrame = -1;

	public override void _ExitTree()
	{
		if (ReferenceEquals(Instance, this))
		{
			Instance = null;
		}
	}

	/// <summary>res:// 和 user:// 要先转成真实路径，System.IO 才读得到。</summary>
	public static string ToFileSystemPath(string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return path;
		}

		return path.StartsWith("res://") || path.StartsWith("user://")
			? ProjectSettings.GlobalizePath(path)
			: path;
	}

	/// <summary>装载 ROM。失败时把原因写进 <see cref="LastError"/> 并发出 <c>RomLoadFailed</c>。</summary>
	public bool LoadRom(string path)
	{
		string filePath = ToFileSystemPath(path);
		if (!Cartridge.TryFromFile(filePath, out Cartridge? cartridge, out string error) || cartridge is null)
		{
			LastError = error;
			GD.PushError($"[godot-nes] ROM 装载失败：{error}");
			EmitSignal(SignalName.RomLoadFailed, filePath, error);
			return false;
		}

		Console.LoadCartridge(cartridge);
		CurrentRomPath = filePath;
		CurrentRomDescription = cartridge.Describe();
		LastError = string.Empty;

		GD.Print($"[godot-nes] ROM: {filePath}");
		GD.Print($"[godot-nes] {CurrentRomDescription}");

		EmitSignal(SignalName.RomLoaded, CurrentRomPath, CurrentRomDescription);
		return true;
	}

	public void Reset() => Console.Reset();

	public void TogglePause() => Console.Paused = !Console.Paused;

	public void StepInstruction() => Console.StepInstruction();

	/// <summary>
	/// 关闭当前 ROM：卸载卡带、清掉 ROM 信息，模拟器回到"空机"状态。
	/// 主界面不会因此停下来，只是没有画面、也不再跑 CPU（真机没卡带时执行的是总线垃圾，没有模拟的意义）。
	/// </summary>
	public void CloseRom()
	{
		Console.EjectCartridge();
		CurrentRomPath = string.Empty;
		CurrentRomDescription = string.Empty;
		LastError = string.Empty;

		GD.Print("[godot-nes] 已关闭 ROM");
		EmitSignal(SignalName.RomClosed);
	}

	/// <summary>当前是否装着卡带。</summary>
	public bool HasRom => Console.Cartridge is not null;

	// ==================================================================
	// 调试快照
	//
	// 为什么要这么绕？因为纯 C# 类型（NesConsole / Cpu / Ppu / Cartridge）对 GDScript
	// 完全不可见（实测 get("Console") 返回 null），而且参数/返回值不是 Variant 兼容类型的
	// C# 方法根本不会进 Godot 的方法表。所以调试信息必须在这里"拍平"成
	// Dictionary / PackedXxxArray 再交给 GDScript 侧。
	//
	// 这些方法只在有调试窗口打开时才会被调用（频率由 autoload/debug_hub.gd 控制）。
	// ==================================================================

	/// <summary>系统级状态（帧号、暂停、ROM 信息、FPS）。</summary>
	public Godot.Collections.Dictionary GetSystemSnapshot()
	{
		Cartridge? cartridge = Console.Cartridge;
		return new Godot.Collections.Dictionary
		{
			{ "frame", Console.FrameCount },
			{ "paused", Console.Paused },
			{ "fps", (int)Engine.GetFramesPerSecond() },
			{ "nes_fps", NesFps },
			{ "rom_name", CurrentRomPath.Length > 0 ? Path.GetFileName(CurrentRomPath) : string.Empty },
			{ "rom_path", CurrentRomPath },
			{ "rom_desc", CurrentRomDescription },
			{ "last_error", LastError },
			{ "has_rom", cartridge is not null },
			{ "mapper", cartridge?.MapperNumber ?? -1 },
			{ "mapper_name", cartridge?.Mapper.Name ?? string.Empty },
			{ "prg_kb", (cartridge?.PrgSize ?? 0) / 1024 },
			{ "chr_kb", cartridge is null || cartridge.UsesChrRam ? 0 : cartridge.ChrSize / 1024 },
			{ "uses_chr_ram", cartridge?.UsesChrRam ?? false },
			// 手柄按键位掩码（bit0=A、bit1=B、bit2=Select、bit3=Start、bit4=Up…bit7=Right），
			// 和 $4016 的移位顺序一致。调试界面和"输入到底有没有传进去"的测试都用它。
			{ "controller1", Console.Bus.Controller1.Buttons },
			{ "controller2", Console.Bus.Controller2.Buttons },
			{ "controller3", Console.Bus.Controller3.Buttons },
			{ "controller4", Console.Bus.Controller4.Buttons },
			{ "four_score", Console.Bus.FourScore },
		};
	}

	/// <summary>
	/// APU 各通道状态。排查"声音不对"的时候最有用：能直接看出通道有没有被使能、
	/// 长度计数器是不是 0、周期是多少、当前输出电平是几。
	/// </summary>
	public Godot.Collections.Dictionary GetApuSnapshot()
	{
		Apu apu = Console.Apu;
		return new Godot.Collections.Dictionary
		{
			{ "pulse1", ChannelSnapshot(apu.Pulse1.Enabled, apu.Pulse1.LengthCounter,
				apu.Pulse1.TimerPeriod, apu.Pulse1.Output()) },
			{ "pulse2", ChannelSnapshot(apu.Pulse2.Enabled, apu.Pulse2.LengthCounter,
				apu.Pulse2.TimerPeriod, apu.Pulse2.Output()) },
			{ "triangle", ChannelSnapshot(apu.Triangle.Enabled, apu.Triangle.LengthCounter,
				apu.Triangle.TimerPeriod, apu.Triangle.Output()) },
			{ "noise", ChannelSnapshot(apu.Noise.Enabled, apu.Noise.LengthCounter,
				apu.Noise.PeriodIndex, apu.Noise.Output()) },
			{ "dmc", ChannelSnapshot(apu.Dmc.Enabled, apu.Dmc.LengthCounter,
				apu.Dmc.RateIndex, apu.Dmc.Output()) },
			{ "frame_irq", apu.FrameIrqPending },
		};
	}

	private static Godot.Collections.Dictionary ChannelSnapshot(
		bool enabled, int length, int period, int output) => new()
	{
		{ "enabled", enabled },
		{ "length", length },
		{ "period", period },
		{ "output", output },
	};

	/// <summary>CPU 寄存器、标志位、栈顶。</summary>
	public Godot.Collections.Dictionary GetCpuSnapshot()
	{
		Cpu cpu = Console.Cpu;
		return new Godot.Collections.Dictionary
		{
			{ "pc", (int)cpu.PC },
			{ "a", (int)cpu.A },
			{ "x", (int)cpu.X },
			{ "y", (int)cpu.Y },
			{ "sp", (int)cpu.SP },
			{ "p", (int)cpu.P },
			{ "cycles", cpu.Cycles },
			{ "flag_n", cpu.GetFlag(StatusFlags.Negative) },
			{ "flag_v", cpu.GetFlag(StatusFlags.Overflow) },
			{ "flag_u", cpu.GetFlag(StatusFlags.Unused) },
			{ "flag_b", cpu.GetFlag(StatusFlags.Break) },
			{ "flag_d", cpu.GetFlag(StatusFlags.Decimal) },
			{ "flag_i", cpu.GetFlag(StatusFlags.InterruptDisable) },
			{ "flag_z", cpu.GetFlag(StatusFlags.Zero) },
			{ "flag_c", cpu.GetFlag(StatusFlags.Carry) },
			{ "illegal_opcode", Console.IllegalOpcode.HasValue ? Console.IllegalOpcode.Value : -1 },
			{ "stack", GetStackSnapshot() },
		};
	}

	/// <summary>PPU 寄存器与状态位。</summary>
	public Godot.Collections.Dictionary GetPpuSnapshot()
	{
		Ppu ppu = Console.Ppu;
		Cartridge? cartridge = Console.Cartridge;
		return new Godot.Collections.Dictionary
		{
			{ "ctrl", (int)ppu.Ctrl },
			{ "mask", (int)ppu.Mask },
			{ "status", (int)ppu.Status },
			{ "oam_addr", (int)ppu.OamAddr },
			{ "vram_addr", (int)ppu.VramAddress },
			{ "increment", ppu.Increment },
			{ "nmi_enabled", ppu.NmiEnabled },
			{ "rendering", ppu.RenderingEnabled },
			{ "sprite_8x16", ppu.SpriteSize8x16 },
			{ "bg_pattern_base", ppu.BackgroundPatternBase },
			{ "sprite_pattern_base", ppu.SpritePatternBase },
			{ "vblank", (ppu.Status & 0x80) != 0 },
			{ "sprite0_hit", (ppu.Status & 0x40) != 0 },
			{ "sprite_overflow", (ppu.Status & 0x20) != 0 },
			// 这里给的是**当前生效**的镜像（mapper 改过就是 mapper 的值），
			// 不是卡带头里那个固定值 —— MMC1/MMC3/AxROM 游戏运行时两者经常不一样。
			{ "mirroring", Cartridge.DescribeMirroring(ppu.EffectiveMirroring) },
			{ "mapper", cartridge?.Mapper.Name ?? string.Empty },
			{ "mapper_irq", cartridge?.Mapper.IrqPending ?? false },
		};
	}

	/// <summary>
	/// 32 字节调色板 RAM，每个值 0-63。
	/// 注意：Godot C# 绑定里 PackedInt32Array 就是 <c>int[]</c>（PackedByteArray 是 byte[]，
	/// PackedColorArray 是 Color[]），传到 GDScript 侧会自动变成对应的 Packed 数组。
	/// </summary>
	public int[] GetPaletteRam()
	{
		byte[] ram = Console.Ppu.PaletteRam;
		var result = new int[ram.Length];
		for (int i = 0; i < ram.Length; i++)
		{
			result[i] = ram[i];
		}

		return result;
	}

	/// <summary>64 个 NES 颜色，供 UI 画色块用（避免在 GDScript 里再抄一份调色板表）。</summary>
	public Color[] GetNesPalette()
	{
		var colors = new Color[NesPalette.ColorCount];
		for (int i = 0; i < colors.Length; i++)
		{
			uint packed = NesPalette.ToPackedRgba(i);
			colors[i] = Color.Color8(
				(byte)(packed >> 24), (byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
		}

		return colors;
	}

	/// <summary>一次拿全（调试窗口每 1/12 秒调用一次）。</summary>
	public Godot.Collections.Dictionary GetDebugSnapshot() => new Godot.Collections.Dictionary
	{
		{ "system", GetSystemSnapshot() },
		{ "cpu", GetCpuSnapshot() },
		{ "ppu", GetPpuSnapshot() },
	};

	/// <summary>
	/// 已经实现的 mapper 号（升序）。ROM 信息窗口和"这个 ROM 能不能跑"的判断都用它，
	/// 免得把"已实现列表"在 GDScript 那边再抄一份。
	/// </summary>
	public int[] GetImplementedMappers()
	{
		var result = new List<int>(MapperFactory.SupportedMappers);
		result.Sort();
		return result.ToArray();
	}

	/// <summary>
	/// ROM 文件与卡带的详细信息（读头得到的"游戏相关信息"）。
	/// 内容基本不变，所以 DebugHub 只在 RomLoaded 信号后重新拉一次，不用每帧取。
	/// </summary>
	public Godot.Collections.Dictionary GetRomInfo()
	{
		Cartridge? cartridge = Console.Cartridge;
		if (cartridge is null)
		{
			return new Godot.Collections.Dictionary { { "has_rom", false } };
		}

		var warnings = new Godot.Collections.Array();
		foreach (string warning in cartridge.Warnings)
		{
			warnings.Add(warning);
		}

		return new Godot.Collections.Dictionary
		{
			{ "has_rom", true },
			{ "file_name", cartridge.SourcePath.Length > 0 ? Path.GetFileName(cartridge.SourcePath) : "(内存)" },
			{ "file_path", cartridge.SourcePath },
			{ "file_size", cartridge.FileSize },
			{ "format", cartridge.IsNes20 ? "NES 2.0" : "iNES 1.0" },
			{ "mapper", cartridge.MapperNumber },
			{ "mapper_name", cartridge.MapperName },
			{ "mapper_implemented", MapperFactory.IsSupported(cartridge.MapperNumber) },
			{ "submapper", cartridge.SubmapperNumber },
			{ "prg_kb", cartridge.PrgSize / 1024 },
			{ "prg_banks", cartridge.PrgBankCount },
			{ "chr_kb", cartridge.ChrSize / 1024 },
			{ "chr_banks", cartridge.ChrBankCount },
			{ "uses_chr_ram", cartridge.UsesChrRam },
			{ "chr_ram_kb", cartridge.ChrRamSize / 1024 },
			{ "chr_nvram_kb", cartridge.ChrNvRamSize / 1024 },
			{ "wram_kb", cartridge.PrgRamSize / 1024 },
			{ "nvram_kb", cartridge.PrgNvRamSize / 1024 },
			{ "mirroring", cartridge.MirroringText },
			{ "tv_system", cartridge.TvSystemText },
			{ "console", cartridge.ConsoleTypeText },
			{ "battery", cartridge.HasBattery },
			{ "trainer", cartridge.HasTrainer },
			{ "nmi_vector", (int)cartridge.NmiVector },
			{ "reset_vector", (int)cartridge.ResetVector },
			{ "irq_vector", (int)cartridge.IrqVector },
			{ "crc32", cartridge.Crc32.ToString("X8") },
			{ "md5", cartridge.Md5 },
			{ "warnings", warnings },
		};
	}

	/// <summary>栈顶 8 个字节（$0100 + SP + 1 起），用来看返回地址。</summary>
	private string GetStackSnapshot()
	{
		var sb = new StringBuilder(24);
		for (int i = 0; i < 8; i++)
		{
			ushort address = (ushort)(0x0100 + ((Console.Cpu.SP + 1 + i) & 0xFF));
			if (i > 0)
			{
				sb.Append(' ');
			}

			sb.Append(Console.Bus.Read(address).ToString("X2"));
		}

		return sb.ToString();
	}
}
