using System;

namespace GodotNes.Core;

/// <summary>
/// 一台完整的 NES：CPU + PPU + APU + 总线 + 卡带。
///
/// 这个类不引用任何 Godot 类型（纯 C#），Godot 只负责驱动它、显示帧缓冲、输出音频，
/// 这样核心逻辑可以单独测试，也可以换成别的前端。
///
/// 时序上三个部件是**同步推进**的：CPU 执行一条指令用了 N 个周期，
/// PPU 就跑 3N 个周期（NTSC 下 PPU 是 CPU 的 3 倍频），APU 跑 N 个周期。
/// 这样 VBlank / NMI 和音频采样率才会落在正确的时刻，
/// 而不是"一帧跑完再补一个 NMI、再一次性生成一帧的音频"。
/// </summary>
public sealed class NesConsole
{
	/// <summary>NTSC 一帧的 CPU 周期数（PAL 是 33247）。</summary>
	public const int CpuCyclesPerFrame = 29780;

	/// <summary>NTSC 一帧的 PPU 周期数，正好是 CPU 的 3 倍。</summary>
	public const int PpuCyclesPerFrame = Ppu.CyclesPerFrame;

	/// <summary>PPU 周期与 CPU 周期的比值。</summary>
	public const int PpuCyclesPerCpuCycle = 3;

	/// <summary>每帧最多产出这么多个音频样本（44100 / 60 ≈ 735，留足余量）。</summary>
	private const int AudioBufferCapacity = 4096;

	private readonly float[] _audioBuffer = new float[AudioBufferCapacity];

	public NesConsole()
	{
		Ppu = new Ppu();
		Apu = new Apu();
		Bus = new Bus(Ppu, Apu);
		Cpu = new Cpu(Bus);
		Apu.Bus = Bus;
	}

	public Bus Bus { get; }

	public Cpu Cpu { get; }

	public Ppu Ppu { get; }

	public Apu Apu { get; }

	public Cartridge? Cartridge { get; private set; }

	/// <summary>音频输出端（Godot 侧由 NesAudioPlayer 实现）。没接就只是把样本丢掉。</summary>
	public IAudioSink? AudioSink { get; set; }

	/// <summary>已经跑完的帧数。</summary>
	public long FrameCount { get; private set; }

	/// <summary>暂停时不推进模拟，但显示和 UI 照常刷新。</summary>
	public bool Paused { get; set; }

	/// <summary>
	/// CPU 撞到的第一条未实现的非法指令（官方 56 条都实现了，所以正常情况下一直是 null）。
	/// UI 里显示这个值，一眼能看出某款游戏用到了哪条非官方指令。
	/// </summary>
	public byte? IllegalOpcode { get; private set; }

	/// <summary>显示用帧缓冲（RGBA8，256x240），就是 PPU 那一块。</summary>
	public byte[] FrameBuffer => Ppu.FrameBuffer;

	/// <summary>装载卡带并复位。</summary>
	public void LoadCartridge(Cartridge cartridge)
	{
		Cartridge = cartridge ?? throw new ArgumentNullException(nameof(cartridge));
		Bus.Cartridge = cartridge;
		Ppu.Mapper = cartridge.Mapper;
		Ppu.Mirroring = cartridge.Mirroring;
		Reset();
	}

	/// <summary>卸载卡带。之后 CPU 不再推进，画面回到背景色。</summary>
	public void EjectCartridge()
	{
		Cartridge = null;
		Bus.Cartridge = null;
		Ppu.Mapper = null;
		Reset();
	}

	/// <summary>复位（清 RAM、取复位向量、PPU 回到扫描线 0、APU 清空、mapper 寄存器归零）。</summary>
	public void Reset()
	{
		Array.Clear(Bus.Ram, 0, Bus.Ram.Length);
		IllegalOpcode = null;
		Cpu.Reset();
		Ppu.Reset();
		Apu.Reset();

		// mapper 的 bank / 镜像寄存器也一起归零：真机上按 Reset 不清它们，
		// 但模拟器里"复位"要的就是一个确定的初始状态，游戏反正会重新初始化一遍。
		Cartridge?.Mapper.Reset();
	}

	/// <summary>
	/// 推进一帧：跑到 **PPU 的帧计数器 +1** 为止（和参照实现同一套语义）。
	///
	/// 这里不能用"固定 29780 个 CPU 周期"来切帧：那样的边界和 PPU 的帧边界差 2 个 PPU dot，
	/// 帧号会慢慢漂；而且 `--dump-frame-after N` 抓到的可能是"上半屏是这一帧、下半屏是上一帧"
	/// 的撕裂画面（踩过：SMB 前几帧整屏都不一样，就是切帧切在画面中间）。
	/// 没有卡带时直接返回 —— 真机此时会在总线上执行垃圾指令，没有模拟的意义。
	/// 撞到非法指令也会停住（并记下 opcode），免得拿着错误状态继续跑下去。
	/// </summary>
	public void StepFrame()
	{
		if (Paused || Cartridge is null)
		{
			return;
		}

		long targetFrame = Ppu.FrameCount + 1;
		while (Ppu.FrameCount < targetFrame && !Cpu.HitIllegalOpcode)
		{
			StepInstruction();
		}

		FlushAudio();
		FrameCount++;
	}

	/// <summary>执行一条 CPU 指令，并让 PPU / APU 同步前进。</summary>
	public void StepInstruction()
	{
		if (Cpu.HitIllegalOpcode)
		{
			return;
		}

		// 中断要在指令之前递送，让 CPU 先跳进处理程序
		if (Ppu.TakeNmiRequest())
		{
			Cpu.Nmi();
		}

		// APU 的帧 IRQ 和 mapper（MMC3 扫描线）的 IRQ 都接在同一根线上。
		// 两边都是"保持型"的：条件还在就会一直被重新拉起，由中断处理程序自己去清。
		if (Apu.FrameIrqPending || Apu.Dmc.IrqPending || (Cartridge?.Mapper.IrqPending ?? false))
		{
			Cpu.Irq();
		}

		long profStart = Profiling ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;

		int cycles = Cpu.Step();

		if (Profiling)
		{
			long afterCpu = System.Diagnostics.Stopwatch.GetTimestamp();
			Ppu.Step(cycles * PpuCyclesPerCpuCycle);
			long afterPpu = System.Diagnostics.Stopwatch.GetTimestamp();
			Apu.Step(cycles);
			long afterApu = System.Diagnostics.Stopwatch.GetTimestamp();

			_cpuTicks += afterCpu - profStart;
			_ppuTicks += afterPpu - afterCpu;
			_apuTicks += afterApu - afterPpu;
			ProfiledInstructions++;
		}
		else
		{
			Ppu.Step(cycles * PpuCyclesPerCpuCycle);
			Apu.Step(cycles);
		}

		if (Cpu.HitIllegalOpcode)
		{
			IllegalOpcode = Cpu.LastOpcode;
		}
	}

	/// <summary>
	/// 打开后按"指令"统计 CPU / PPU / APU 各花了多少时间（<see cref="System.Diagnostics.Stopwatch"/>，
	/// 纯 .NET，core 层不认识 Godot）。默认关着 —— 打开会有每指令两三次取时间戳的开销，
	/// 只用来排查"到底哪一块慢"，不要让它在正常运行时开着。
	/// </summary>
	public bool Profiling { get; set; }

	/// <summary>本轮统计里 CPU 占的毫秒数（<see cref="Profiling"/> 打开时才有意义）。</summary>
	public double ProfiledCpuMs { get; private set; }

	public double ProfiledPpuMs { get; private set; }

	public double ProfiledApuMs { get; private set; }

	/// <summary>本轮统计覆盖了多少条指令。</summary>
	public long ProfiledInstructions { get; private set; }

	private long _cpuTicks;
	private long _ppuTicks;
	private long _apuTicks;

	/// <summary>把本轮统计结算成"每帧毫秒"并清零。</summary>
	public void FinishProfilingWindow(long frames)
	{
		if (!Profiling || frames <= 0)
		{
			return;
		}

		double ticksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;
		ProfiledCpuMs = _cpuTicks / ticksPerMs / frames;
		ProfiledPpuMs = _ppuTicks / ticksPerMs / frames;
		ProfiledApuMs = _apuTicks / ticksPerMs / frames;
		_cpuTicks = 0;
		_ppuTicks = 0;
		_apuTicks = 0;
		ProfiledInstructions = 0;
	}

	/// <summary>把 APU 攒下的样本交给音频输出端。没接输出端就直接丢掉，避免缓冲一直积压。</summary>
	private void FlushAudio()
	{
		int count = Apu.DrainSamples(_audioBuffer);
		if (count > 0)
		{
			AudioSink?.Write(_audioBuffer.AsSpan(0, count));
		}
	}
}
