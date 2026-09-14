using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;
using GodotNes.Core;

namespace GodotNes.Game;

/// <summary>
/// 模拟器的 Godot 侧驱动节点（C#）。
///
/// 分工：控制流和 UI 都归 GDScript（<c>ui/main.gd</c>），这个节点只提供
/// 「一次调用跑完一整帧」的入口。之所以把整帧塞进一个 C# 方法里，有两个原因：
///   1. 帧缓冲 245 KB 绝不能跨语言传递；
///   2. <see cref="InputAdapter.PushTo"/> 的参数是 NesConsole（纯 C# 类型），
///      GDScript 本来就调不到它（实测 has_method == false）。
///
/// 支持的命令行参数（写在 <c>--</c> 之后）：
/// <c>--rom &lt;路径&gt;</c>             启动时装载 ROM（也可以直接给一个 .nes 路径）
/// <c>--rom-info</c>                 只解析并打印 ROM 头信息，然后退出（无头验证用）
/// <c>--trace-cpu &lt;条数&gt;</c>         不跑帧循环，单步执行 N 条指令并写 trace 后退出
/// <c>--trace-out &lt;路径&gt;</c>         配合 --trace-cpu 指定输出文件
/// <c>--dump-frame &lt;png 路径&gt;</c>    跑若干帧后把帧缓冲写成 PNG 然后退出
/// <c>--dump-frame-indices &lt;路径&gt;</c> 同上，但写的是 PPU 的调色板索引帧（逐字节比对用）
/// <c>--dump-frame-after &lt;帧数&gt;</c>  配合上面两个用，默认第 1 帧
/// </summary>
public partial class EmulatorCore : Node
{
	private Display? _display;
	private InputAdapter? _inputAdapter;
	private NesAudioPlayer? _audioPlayer;
	private EmulatorService? _service;

	/// <summary><c>--dump-audio</c> 时把样本收集到这里，而不是送去播放。</summary>
	private AudioCollector? _audioCollector;

	private string _romPath = string.Empty;
	private string _frameDumpPath = string.Empty;
	private string _indexDumpPath = string.Empty;
	private string _audioDumpPath = string.Empty;
	private int _ramDumpAddress;
	private int _ramDumpLength;
	private int _frameDumpDelay = 1;
	private bool _romInfoOnly;
	private int _traceCount;
	private string _tracePath = string.Empty;

	/// <summary><c>--trace-console</c>：trace 时让整机一起跑，而不是只单步 CPU。</summary>
	private bool _traceWithConsole;

	/// <summary><c>--trace-skip N</c>：trace 前先空跑 N 条指令（查"跑到第几千帧才卡住"用）。</summary>
	private int _traceSkip;

	/// <summary>
	/// <c>--trace-from-frame N</c>：先按帧跑到第 N 帧，再开始记 trace。
	/// 和参照实现对比"进关卡那一刻哪条指令分岔"必须用这个 —— 两边都从**同一帧边界**开始，
	/// trace 才能逐行对齐；按指令条数对齐是不行的（两边的指令/周期比不一样）。
	/// </summary>
	private int _traceFromFrame;

	/// <summary><c>--log-mapper &lt;路径&gt;</c>：把映射器寄存器写记成 "帧号 地址 值"。</summary>
	private string _mapperLogPath = string.Empty;

	private StreamWriter? _mapperLog;

	/// <summary><c>--log-ppu &lt;路径&gt;</c>：把 PPU 寄存器写记成 "帧 扫描线 寄存器 值"。</summary>
	private string _ppuLogPath = string.Empty;

	private StreamWriter? _ppuLog;

	/// <summary><c>--log-apu &lt;路径&gt;</c>：把每次 $4015 读写的值和各通道长度计数器记下来。</summary>
	private string _apuLogPath = string.Empty;

	private StreamWriter? _apuLog;

	/// <summary><c>--log-sprites &lt;路径&gt;</c>：记底栏那一段每行的精灵评估数量，并在某一帧把 OAM 全表打出来。</summary>
	private string _spriteLogPath = string.Empty;

	private StreamWriter? _spriteLog;

	/// <summary><c>--log-input &lt;路径&gt;</c>：把每帧的手柄按键记录下来（只在变化时写一行），用于复现手动操作。</summary>
	private string _inputLogPath = string.Empty;

	/// <summary><c>--press-file &lt;路径&gt;</c>：按 <c>--log-input</c> 记录下来的格式（"帧 按键位"）重放一段手动操作。</summary>
	private string _pressFilePath = string.Empty;

	private StreamWriter? _inputLog;

	/// <summary><c>--ram-trace &lt;路径&gt;</c>：每帧把 2KB 主 RAM 追加写入，用于和 FCEUX 逐帧对比找状态分岔点。</summary>
	private string _ramTracePath = string.Empty;

	private FileStream? _ramTrace;

	private int _lastLoggedButtons = -1;

	/// <summary>是否插了四人分插器（见 SetFourScore）。</summary>
	private bool _fourScore;

	private readonly List<(long Frame, int Buttons)> _recordedPresses = new();

	private int _recordedIndex;

	private int _recordedButtons;

	/// <summary><c>--log-ppu-scroll</c>：连每条关键扫描线的卷轴状态一起记。</summary>
	private bool _ppuScrollLog;

	/// <summary>
	/// <c>--press 键 帧号 [松开帧号]</c>（可重复）：从第 N 帧起按住手柄 1 的某个键，
	/// 给了松开帧号就在那一帧松开。只是给无头测试用的钩子 —— 没有它就没法在命令行里
	/// "开始游戏"，也就没法比对关卡音乐 / 帧画面，更没法复现"先按方向再按 Start"这类操作。
	/// </summary>
	private readonly List<(NesButton Button, int DownFrame, int UpFrame)> _scriptedPresses = new();

	/// <summary>NTSC 一帧的真实时长（60.0988 Hz）。</summary>
	private const double TargetFrameSeconds = 1.0 / 60.0988;

	/// <summary>一次调用最多补几帧，免得卡顿之后"追帧"追出个死循环。</summary>
	private const int MaxFramesPerTick = 4;

	/// <summary>单次 delta 最多算这么多秒（切窗口回来时 delta 会很大）。</summary>
	private const double MaxCatchUpSeconds = 0.25;

	/// <summary>累计还没跑的时间（秒）。</summary>
	private double _frameAccumulator;

	/// <summary>无头 / 命令行模式下的物理步频（见 <see cref="_Ready"/> 里的说明）。</summary>
	private const int HeadlessPhysicsTicksPerSecond = 1000;

	/// <summary>性能统计的窗口长度（微秒）。</summary>
	private const ulong ProfileWindowMicros = 1_000_000;

	private ulong _profileWindowStart;
	private ulong _emulationMicros;
	private ulong _presentMicros;
	private int _profiledFrames;

	/// <summary>最近一秒里，跑一个 NES 帧平均花在"模拟"上的微秒数。</summary>
	public double EmulationMicrosPerFrame { get; private set; }

	/// <summary>最近一秒里，跑一个 NES 帧平均花在"上传纹理"上的微秒数。</summary>
	public double PresentMicrosPerFrame { get; private set; }

	/// <summary>
	/// 打开后按指令统计 CPU / PPU / APU 各占多少（排查"哪一块慢"用）。
	/// GDScript 侧通过这个开关打开；core 层认的是 <c>NesConsole.Profiling</c>。
	/// </summary>
	public bool ProfileComponents
	{
		get => _service?.Console.Profiling ?? false;
		set
		{
			if (_service is not null)
			{
				_service.Console.Profiling = value;
			}
		}
	}

	/// <summary>上面那个开关打开时：每帧花在 CPU / PPU / APU 上的毫秒数。</summary>
	public double ProfiledCpuMsPerFrame => _service?.Console.ProfiledCpuMs ?? 0.0;

	public double ProfiledPpuMsPerFrame => _service?.Console.ProfiledPpuMs ?? 0.0;

	public double ProfiledApuMsPerFrame => _service?.Console.ProfiledApuMs ?? 0.0;

	/// <summary>无头 / 命令行模式：不做节流，能跑多快跑多快。</summary>
	private bool _unthrottled;

	public override void _Ready()
	{
		ParseCommandLine();

		// 无头（跑测试、导帧）时不节流：测试要的是"第 N 帧"这种确定的结果，不是真实速度。
		_unthrottled = DisplayServer.GetName() == "headless";
		if (_unthrottled)
		{
			// 主场景是用 _physics_process（固定步长）驱动模拟器的，而无头/CLI 模式下
			// Godot 的物理步是按**真实时间**走的（60 步/秒）—— 那样导 1800 帧就得等 30 秒。
			// 这里把物理步频提上去，让 dump / trace 尽快跑完；窗口模式保持真机帧率。
			Engine.PhysicsTicksPerSecond = HeadlessPhysicsTicksPerSecond;
		}

		_service = EmulatorService.Instance;
		_display = GetNodeOrNull<Display>("%Display");
		_inputAdapter = GetNodeOrNull<InputAdapter>("%InputAdapter");
		_audioPlayer = GetNodeOrNull<NesAudioPlayer>("%NesAudioPlayer");

		if (_service is null)
		{
			GD.PushError("[godot-nes] 找不到 EmulatorService 自动加载，模拟器不会运行。");
			return;
		}

		if (_display is null)
		{
			GD.PushError("[godot-nes] 场景里找不到唯一名 %Display 的节点，画面无法显示。");
		}

		if (_inputAdapter is null)
		{
			GD.PushWarning("[godot-nes] 场景里找不到唯一名 %InputAdapter 的节点，手柄输入不会生效。");
		}

		// 音频输出端：平时送去播放器；要导出音频做测试时改成收集器
		if (_audioDumpPath.Length > 0)
		{
			_audioCollector = new AudioCollector();
			_service.Console.AudioSink = _audioCollector;
		}
		else
		{
			_service.Console.AudioSink = _audioPlayer;

		// 关闭 ROM 时清屏。挂信号而不是让界面手动调：菜单、测试、将来别的入口
		// 都会经过 CloseRom()，这样一条路径都不会漏。
		_service.RomClosed += ClearDisplay;
		}

		if (_romPath.Length > 0)
		{
			_service.LoadRom(_romPath);
		}

		// --log-mapper：把每次映射器寄存器写记成 "帧号 地址 值"。
		// 查"分屏换 bank 换错"时，这条日志比整机 trace 便宜得多，而且直接回答
		// "是游戏写的值不同，还是我解码不同"。
		if (_mapperLogPath.Length > 0 && _service.Console.Cartridge is not null)
		{
			_mapperLog = new StreamWriter(_mapperLogPath) { AutoFlush = true };
			NesConsole loggedConsole = _service.Console;
			loggedConsole.Cartridge.Mapper.RegisterWriteLogger = (address, value) =>
				_mapperLog?.WriteLine($"{loggedConsole.FrameCount} {loggedConsole.Ppu.DbgScanline} {address:X4} {value:X2}");

			if (_ramTracePath.Length > 0)
			{
				_ramTrace = new FileStream(_ramTracePath, FileMode.Create, System.IO.FileAccess.Write);
			}

			if (_inputLogPath.Length > 0)
			{
				_inputLog = new StreamWriter(_inputLogPath) { AutoFlush = true };
			}

			if (_pressFilePath.Length > 0 && File.Exists(_pressFilePath))
			{
				foreach (string line in File.ReadAllLines(_pressFilePath))
				{
					string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
					if (parts.Length >= 2 && long.TryParse(parts[0], out long f) && int.TryParse(parts[1], NumberStyles.HexNumber, null, out int b))
					{
						_recordedPresses.Add((f, b));
					}
				}

				_recordedPresses.Sort((x, y) => x.Frame.CompareTo(y.Frame));
				GD.Print($"[godot-nes] 已载入 {_recordedPresses.Count} 条按键记录：{_pressFilePath}");
			}

			if (_spriteLogPath.Length > 0)
			{
				_spriteLog = new StreamWriter(_spriteLogPath) { AutoFlush = true };
				NesConsole spriteConsole = loggedConsole;
				spriteConsole.Ppu.SpriteLineLogger = (scanline, evaluated, drawn, limitHit) =>
				{
					if (scanline == 190 || scanline == 192 || scanline == 194 || scanline == 200
						|| scanline == 210 || scanline == 239)
					{
						_spriteLog?.WriteLine($"{spriteConsole.FrameCount} line={scanline} eval={evaluated} drawn={drawn} limit={(limitHit ? 1 : 0)}");
					}

					if (scanline == 192)
					{
						byte[] oam = spriteConsole.Ppu.DbgOam;
						for (int s = 0; s < 64; s++)
						{
							int o = s * 4;
							if (oam[o] < 0xF0)
							{
								_spriteLog?.WriteLine($"{spriteConsole.FrameCount} OAM{s:D2} y={oam[o]} tile={oam[o + 1]:X2} attr={oam[o + 2]:X2} x={oam[o + 3]}");
							}
						}
					}
				};
			}

			if (_apuLogPath.Length > 0)
			{
				_apuLog = new StreamWriter(_apuLogPath) { AutoFlush = true };
				NesConsole apuConsole = loggedConsole;
				apuConsole.Apu.StatusAccessHook = kind =>
					_apuLog?.WriteLine($"{apuConsole.FrameCount} {apuConsole.Ppu.DbgScanline} {kind} "
						+ $"len={apuConsole.Apu.Pulse1.LengthCounter},{apuConsole.Apu.Pulse2.LengthCounter},"
						+ $"{apuConsole.Apu.Triangle.LengthCounter},{apuConsole.Apu.Noise.LengthCounter} "
						+ $"en={apuConsole.Apu.Pulse1.Enabled},{apuConsole.Apu.Pulse2.Enabled},"
						+ $"{apuConsole.Apu.Triangle.Enabled},{apuConsole.Apu.Noise.Enabled}");
			}

			if (_ppuLogPath.Length > 0)
			{
				_ppuLog = new StreamWriter(_ppuLogPath) { AutoFlush = true };
				loggedConsole.Ppu.RegisterWriteLogger = (register, value, scanline) =>
					_ppuLog?.WriteLine($"{loggedConsole.FrameCount} {scanline} {register:X4} {value:X2}");

				// 每帧只记第 32 行之前和 190 行之后这几条关键扫描线的卷轴状态，
				// 免得日志太吵（这两处正是游戏做分屏的地方）。
				if (_ppuScrollLog)
				{
					loggedConsole.Ppu.ScanlineLogger = (scanline) =>
					{
						if (scanline <= 34 || (scanline >= 190 && scanline <= 200) || scanline == 239)
						{
							_ppuLog?.WriteLine(
								$"S {loggedConsole.FrameCount} {scanline} fineX={loggedConsole.Ppu.DbgFineXScroll}"
								+ $" t=${loggedConsole.Ppu.DbgTempAddress:X4} base={loggedConsole.Ppu.DbgScrollBaseLine}");
						}
					};
				}
			}
		}

		if (_romInfoOnly)
		{
			ReportRomInfoAndQuit();
			return;
		}

		if (_traceCount > 0)
		{
			RunCpuTrace();
		}
	}

	/// <summary>
	/// <c>--trace-cpu N</c>：不进帧循环，直接单步执行 N 条指令，把每条指令**执行前**的
	/// CPU 状态写成一行。用来和参照实现（fogleman/nes）逐行对比，格式见 <see cref="Cpu.FormatTrace"/>。
	///
	/// <c>--trace-console N</c>：同上，但**整机一起跑**（PPU / APU 同步推进、NMI 和 IRQ 照常递送）。
	/// 前者只适合测 CPU 指令本身（PPU 不动，游戏会卡在等 VBlank 的循环里）；
	/// 要查"游戏跑起来之后哪一步和参照实现分岔"，必须用后者。
	/// </summary>
	private void RunCpuTrace()
	{
		if (_service is null)
		{
			return;
		}

		string path = _tracePath.Length > 0 ? _tracePath : "cpu-trace.txt";
		NesConsole console = _service.Console;
		Cpu cpu = console.Cpu;

		// --trace-from-frame N：先按帧跑到第 N 帧（按键脚本照常生效），再从那里开始记。
		for (int frame = 0; frame < _traceFromFrame; frame++)
		{
			ApplyScriptedPresses(console);
			ApplyFourScore();

		console.StepFrame();
		}

		using (var writer = new StreamWriter(path) { AutoFlush = true })
		{
			// --trace-skip N：先空跑 N 条指令（不写出来）再开始记。
			// 查"游戏跑到第几千帧卡住了"时必须用这个：从开机一路 trace 到那里是几千万行。
			for (int i = 0; i < _traceCount + _traceSkip && !cpu.HitIllegalOpcode; i++)
			{
				if (_traceWithConsole)
				{
					ApplyScriptedPresses(console);
				}

				if (i >= _traceSkip)
				{
					writer.WriteLine(cpu.FormatTrace());
				}

				if (_traceWithConsole)
				{
					console.StepInstruction();
				}
				else
				{
					cpu.Step();
				}
			}

			if (cpu.HitIllegalOpcode)
			{
				writer.WriteLine($"# 停：非法指令 ${cpu.LastOpcode:X2}");
			}
		}

		GD.Print($"[godot-nes] CPU trace 已写出：{path}");
		GetTree().Quit(0);
	}

	/// <summary>
	/// <c>--rom-info</c>：把 .nes 头的解析结果打印出来就退出，不进游戏循环。
	/// 用来做无头验证，也能在 CI 里当解析器的回归测试；解析失败时退出码为 1。
	/// </summary>
	private void ReportRomInfoAndQuit()
	{
		Cartridge? cartridge = _service?.Console.Cartridge;
		if (cartridge is null)
		{
			GD.PrintErr($"[godot-nes] 读取 ROM 失败：{_service?.LastError}");
			GetTree().Quit(1);
			return;
		}

		GD.Print("[godot-nes] ROM 信息");
		GD.Print(cartridge.DescribeDetailed());
		GetTree().Quit(0);
	}

	/// <summary>
	/// 给 GDScript 的唯一热路径入口：采样输入 → 按**真实时间**跑够帧数 → 上传纹理。
	/// 每帧只在 GDScript 与 C# 之间跳一次，而且不传任何参数、不返回任何东西。
	///
	/// 由主场景的 <c>_physics_process</c>（**固定步长**，默认 60 步/秒）驱动。
	/// 用固定步长而不是 <c>_process</c>：<c>_process</c> 的步长跟着显示刷新率走
	/// （165Hz 屏上每步 6ms），模拟器的节奏就被显示器带跑了；物理步长是常量，节奏稳。
	///
	/// 但物理步长（1/60 = 16.667ms）和 NTSC 真机的帧长（1/60.0988 = 16.639ms）**并不相等**，
	/// 所以这里还是按时间累积：每步加 1/60 秒、够 16.639ms 就跑一帧。
	/// 结果是绝大多数步跑 1 帧、大约每 607 步多跑 1 帧（60.0988 = 60 × 1.001646），
	/// 长期平均正好贴着真机；反过来，如果每步硬跑一帧，游戏会慢 0.16%。
	///
	/// 无头 / 命令行模式（跑测试、导帧）不节流，一次调用跑一帧，并顺带把物理步频拉高（见 _Ready）。
	/// </summary>
	public void StepAndPresent()
	{
		if (_service is null)
		{
			return;
		}

		NesConsole console = _service.Console;

		if (_unthrottled)
		{
			// 无头 / 命令行模式：能跑多快跑多快（测试要的是确定的帧数，不是真实速度）
			RunOneFrame(console);
			return;
		}

		// 固定步长模式下来的是"物理步"的 delta（常量），不是"显示帧"的 delta
		double delta = GetPhysicsProcessDeltaTime();
		if (delta > MaxCatchUpSeconds)
		{
			delta = MaxCatchUpSeconds;      // 卡了一下也别想"追"回几十帧
		}

		_frameAccumulator += delta;

		int frames = 0;
		while (_frameAccumulator >= TargetFrameSeconds && frames < MaxFramesPerTick)
		{
			_frameAccumulator -= TargetFrameSeconds;
			RunOneFrame(console);
			frames++;
		}
	}

	/// <summary>
	/// 清空画面。关闭 ROM 时由 GDScript 侧调用 —— 卸载卡带之后 CPU 不再跑，
	/// 不清屏的话上一帧会一直挂在屏幕上，看着像还装着 ROM。
	/// </summary>
	/// <summary>
	/// 开关四人分插器（Four Score）。由 GDScript 侧按输入配置在启动时调用；
	/// 之后每帧开跑之前会同步到总线上（装载 ROM 的时机不影响）。
	/// </summary>
	public void SetFourScore(bool enabled)
	{
		_fourScore = enabled;
		ApplyFourScore();
	}

	private void ApplyFourScore()
	{
		if (_service is not null && _service.HasRom && _service.Console.Bus.FourScore != _fourScore)
		{
			_service.Console.Bus.FourScore = _fourScore;
		}
	}

	public void ClearDisplay() => _display?.Clear();

	/// <summary>清屏调用次数（测试用）。</summary>
	public int DisplayClearCount => _display?.ClearCount ?? 0;

	/// <summary>跑一个 NES 帧：采样输入 → StepFrame → 有新画面就上传 → 该 dump 就 dump。</summary>
	private void RunOneFrame(NesConsole console)
	{
		ulong startMicros = Time.GetTicksUsec();

		// 顺序很重要：先把这一帧的输入写进手柄寄存器，再跑模拟。
		_inputAdapter?.PushTo(console);
		ApplyScriptedPresses(console);

		// 记录手动按键：只在按键位发生变化时写一行，配合 --press 就能完整复现一段操作
		if (_inputLog != null)
		{
			int buttons = console.Bus.Controller1.Buttons;
			if (buttons != _lastLoggedButtons)
			{
				_inputLog.WriteLine($"{console.FrameCount} {buttons:X2}");
				_lastLoggedButtons = buttons;
			}
		}

		if (_ramTrace != null)
		{
			// 记录"这一步之前"的 RAM，保证和 FCEUX 的逐帧记录对齐
			_ramTrace.Write(console.Bus.Ram, 0, 0x800);
		}

		console.StepFrame();

		ulong emulatedMicros = Time.GetTicksUsec();

		// StepFrame 跑到 PPU 的帧边界为止，所以这里拿到的是完整的一帧（不会撕裂）
		bool frameReady = console.Ppu.TakeFrameReady();
		if (frameReady)
		{
			_display?.Present(console.FrameBuffer);
		}

		ulong presentedMicros = Time.GetTicksUsec();

		// 性能分解：把一个 NES 帧的开销拆成"跑模拟"和"上传纹理"两段。
		// 排查"游戏不流畅"时最要紧的就是这个 —— 到底是模拟器算不过来，还是画面那边拖的。
		_emulationMicros += emulatedMicros - startMicros;
		_presentMicros += presentedMicros - emulatedMicros;
		_profiledFrames++;

		if (presentedMicros - _profileWindowStart >= ProfileWindowMicros)
		{
			double frames = Math.Max(1, _profiledFrames);
			EmulationMicrosPerFrame = _emulationMicros / frames;
			PresentMicrosPerFrame = _presentMicros / frames;

			// 顺手把"按指令统计"的那份也结算掉（没开就是空转）
			if (_service is not null && _service.Console.Profiling)
			{
				_service.Console.FinishProfilingWindow(_profiledFrames);
			}

			_profiledFrames = 0;
			_emulationMicros = 0;
			_presentMicros = 0;
			_profileWindowStart = presentedMicros;
		}

		if ((_frameDumpPath.Length > 0 || _indexDumpPath.Length > 0
			|| _ramDumpLength > 0 || _audioDumpPath.Length > 0)
			&& frameReady && console.FrameCount >= _frameDumpDelay)
		{
			DumpFrameAndQuit(console);
		}
	}

	/// <summary>
	/// <c>--dump-frame-indices</c>：把 PPU 的调色板索引帧（256x240 字节，每字节 0-63）
	/// 原样写出来。比 PNG 更适合和参照实现逐字节比对 —— 不受调色板表示差异影响。
	/// </summary>
	public string SaveIndexFrame(string path)
	{
		if (_service is null)
		{
			return "EmulatorServiceNotReady";
		}

		try
		{
			File.WriteAllBytes(path, _service.Console.Ppu.IndexBuffer);
		}
		catch (IOException exception)
		{
			return exception.Message;
		}

		GD.Print($"[godot-nes] 已写出索引帧：{path}");
		return string.Empty;
	}

	/// <summary>
	/// 把当前帧缓冲写成 PNG，返回空字符串表示成功，否则返回错误名。
	/// 调试 PPU 时很有用：不用开窗口也能看到画面。
	/// </summary>
	public string SaveFramePng(string path)
	{
		if (_service is null)
		{
			return "EmulatorServiceNotReady";
		}

		var image = Image.CreateFromData(
			Ppu.ScreenWidth, Ppu.ScreenHeight, false, Image.Format.Rgba8, _service.Console.FrameBuffer);

		Error error = image.SavePng(path);
		if (error == Error.Ok)
		{
			GD.Print($"[godot-nes] 已写出帧快照：{path}");
			return string.Empty;
		}

		GD.PushError($"[godot-nes] 写出帧快照失败（{path}）：{error}");
		return error.ToString();
	}

	private void DumpFrameAndQuit(NesConsole console)
	{
		if (_frameDumpPath.Length > 0)
		{
			string error = SaveFramePng(_frameDumpPath);
			if (error.Length > 0)
			{
				GD.PushError($"[godot-nes] 帧快照写入失败：{error}");
			}
		}

		if (_indexDumpPath.Length > 0)
		{
			string error = SaveIndexFrame(_indexDumpPath);
			if (error.Length > 0)
			{
				GD.PushError($"[godot-nes] 索引帧写入失败：{error}");
			}
		}

		if (_ramDumpLength > 0)
		{
			DumpRam(console);
		}

		if (_audioDumpPath.Length > 0)
		{
			SaveAudio(_audioDumpPath);
		}

		GetTree().Quit();
	}

	/// <summary><c>--dump-audio &lt;路径&gt;</c>：把这一帧收到的音频写成 16 位单声道 PCM（44100 Hz）。</summary>
	private void SaveAudio(string path)
	{
		if (_audioCollector is null)
		{
			return;
		}

		short[] samples = _audioCollector.ToArray();

		// 小端 16 位 PCM，方便用脚本直接读
		var bytes = new byte[samples.Length * 2];
		for (int i = 0; i < samples.Length; i++)
		{
			bytes[(i * 2) + 0] = (byte)(samples[i] & 0xFF);
			bytes[(i * 2) + 1] = (byte)((samples[i] >> 8) & 0xFF);
		}

		try
		{
			File.WriteAllBytes(path, bytes);
		}
		catch (IOException exception)
		{
			GD.PushError($"[godot-nes] 音频写入失败：{exception.Message}");
			return;
		}

		GD.Print($"[godot-nes] 已写出音频：{path}（{samples.Length} 个样本）");
	}

	/// <summary><c>--dump-ram &lt;地址&gt; &lt;长度&gt;</c>：把一段 CPU RAM 按十六进制打到标准输出。</summary>
	private void DumpRam(NesConsole console)
	{
		var sb = new StringBuilder((_ramDumpLength * 3) + 32);
		sb.Append($"[godot-nes] RAM ${_ramDumpAddress:X4}:");

		for (int i = 0; i < _ramDumpLength; i++)
		{
			int address = (_ramDumpAddress + i) & 0xFFFF;
			sb.Append(' ').Append(console.Bus.Read((ushort)address).ToString("X2"));
		}

		GD.Print(sb.ToString());
	}

	/// <summary>把 "0300" 这样的十六进制字符串解析成整数。</summary>
	private static bool TryParseHex(string text, out int value) =>
		int.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out value);

	/// <summary>把 <c>--press</c> 的按键名解析成 <see cref="NesButton"/>。</summary>
	private static bool TryParseButton(string text, out NesButton button)
	{
		switch (text.ToLowerInvariant())
		{
			case "a": button = NesButton.A; return true;
			case "b": button = NesButton.B; return true;
			case "select": button = NesButton.Select; return true;
			case "start": button = NesButton.Start; return true;
			case "up": button = NesButton.Up; return true;
			case "down": button = NesButton.Down; return true;
			case "left": button = NesButton.Left; return true;
			case "right": button = NesButton.Right; return true;
			default: button = NesButton.A; return false;
		}
	}

	private void ParseCommandLine()
	{
		string[] args = OS.GetCmdlineUserArgs();

		for (int i = 0; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--rom":
				case "-r":
					if (i + 1 < args.Length)
					{
						_romPath = args[++i];
					}

					break;

				case "--rom-info":
					_romInfoOnly = true;
					break;

				case "--trace-cpu":
					if (i + 1 < args.Length && int.TryParse(args[i + 1], out int traceCount))
					{
						_traceCount = traceCount;
						i++;
					}

					break;

				case "--trace-console":
					if (i + 1 < args.Length && int.TryParse(args[i + 1], out int consoleTraceCount))
					{
						_traceCount = consoleTraceCount;
						_traceWithConsole = true;
						i++;
					}

					break;

				case "--trace-out":
					if (i + 1 < args.Length)
					{
						_tracePath = args[++i];
					}

					break;

				case "--trace-skip":
					if (i + 1 < args.Length && int.TryParse(args[i + 1], out int traceSkip))
					{
						_traceSkip = traceSkip;
						i++;
					}

					break;

				case "--trace-from-frame":
					if (i + 1 < args.Length && int.TryParse(args[i + 1], out int traceFrom))
					{
						_traceFromFrame = traceFrom;
						i++;
					}

					break;

				case "--log-mapper":
					if (i + 1 < args.Length)
					{
						_mapperLogPath = args[++i];
					}

					break;

				case "--log-ppu-scroll":
					_ppuScrollLog = true;
					break;

				case "--press-file":
					if (i + 1 < args.Length)
					{
						_pressFilePath = args[++i];
					}

					break;

				case "--four-score":
					_fourScore = true;
					break;

				case "--ram-trace":
					if (i + 1 < args.Length)
					{
						_ramTracePath = args[++i];
					}

					break;

				case "--log-input":
					if (i + 1 < args.Length)
					{
						_inputLogPath = args[++i];
					}

					break;

				case "--log-sprites":
					if (i + 1 < args.Length)
					{
						_spriteLogPath = args[++i];
					}

					break;

				case "--log-apu":
					if (i + 1 < args.Length)
					{
						_apuLogPath = args[++i];
					}

					break;

				case "--log-ppu":
					if (i + 1 < args.Length)
					{
						_ppuLogPath = args[++i];
					}

					break;

				case "--dump-frame":
					if (i + 1 < args.Length)
					{
						_frameDumpPath = args[++i];
					}

					break;

				case "--dump-frame-indices":
					if (i + 1 < args.Length)
					{
						_indexDumpPath = args[++i];
					}

					break;

				case "--dump-ram":
					if (i + 2 < args.Length && TryParseHex(args[i + 1], out int ramAddress)
						&& int.TryParse(args[i + 2], out int ramLength))
					{
						_ramDumpAddress = ramAddress;
						_ramDumpLength = ramLength;
						i += 2;
					}

					break;

				case "--dump-audio":
					if (i + 1 < args.Length)
					{
						_audioDumpPath = args[++i];
					}

					break;

				case "--dump-frame-after":
					if (i + 1 < args.Length && int.TryParse(args[i + 1], out int delay))
					{
						_frameDumpDelay = delay;
						i++;
					}

					break;

				case "--press":
					if (i + 2 < args.Length && TryParseButton(args[i + 1], out NesButton pressed)
						&& int.TryParse(args[i + 2], out int pressFrame))
					{
						int releaseFrame = int.MaxValue;
						if (i + 3 < args.Length && int.TryParse(args[i + 3], out int parsedRelease))
						{
							releaseFrame = parsedRelease;
							i++;
						}

						_scriptedPresses.Add((pressed, pressFrame, releaseFrame));
						i += 2;
					}

					break;

				default:
					if (_romPath.Length == 0 && args[i].EndsWith(".nes", StringComparison.OrdinalIgnoreCase))
					{
						_romPath = args[i];
					}

					break;
			}
		}
	}

	/// <summary>
	/// 记录"当前就在这里"的完整快照（界面上按 Q 触发）。
	/// 存下这一帧的画面 + 名称表/属性表 + 当前映射出来的 CHR + OAM + 调色板 + RAM，
	/// 这样后面可以离线把这一帧原样重画出来，逐块比对底部 UI 到底读的是哪块名称表。
	/// </summary>
	public void MarkPlayPosition()
	{
		NesConsole? console = _service?.Console;
		if (console is null)
		{
			return;
		}

		string dir = Path.GetDirectoryName(_inputLogPath) ?? ".";
		long frame = console.FrameCount;
		string basePath = Path.Combine(dir, $"mark-{frame}");

		File.WriteAllBytes(basePath + ".idx", console.Ppu.IndexBuffer);
		File.WriteAllBytes(basePath + ".oam", console.Ppu.DbgOam);
		File.WriteAllBytes(basePath + ".ram", console.Bus.Ram);
		File.WriteAllBytes(basePath + ".pal", console.Ppu.DbgPaletteRam);

		byte[] nametables = new byte[0x1000];
		for (int i = 0; i < 0x1000; i++)
		{
			nametables[i] = console.Ppu.ReadVram((ushort)(0x2000 + i));
		}

		File.WriteAllBytes(basePath + ".nt", nametables);

		byte[] chr = new byte[0x2000];
		for (int i = 0; i < 0x2000; i++)
		{
			chr[i] = console.Ppu.ReadVram((ushort)i);
		}

		File.WriteAllBytes(basePath + ".chr", chr);
		File.AppendAllText(Path.Combine(dir, "marks.txt"), $"{frame}" + "\n");
		GD.Print($"[godot-nes] 已记录第 {frame} 帧的完整快照（{basePath}.*）");
	}
	/// <summary>给无头测试用：按 <c>--press</c> 指定的帧号按住 / 松开手柄 1 的键。</summary>
	private void ApplyScriptedPresses(NesConsole console)
	{
		if (_scriptedPresses.Count == 0 && _recordedPresses.Count == 0)
		{
			return;
		}

		long frame = console.FrameCount;

		// 先按 --press-file 里记录的手动操作重放（和 --press 叠加使用）
		if (_recordedPresses.Count > 0)
		{
			int index = _recordedIndex;
			int buttons = _recordedButtons;
			while (index < _recordedPresses.Count && _recordedPresses[index].Frame <= frame)
			{
				buttons = _recordedPresses[index].Buttons;
				index++;
			}

			_recordedIndex = index;
			_recordedButtons = buttons;

			// 按键位 → NES 按键（bit0=A, bit1=B, bit2=Select, bit3=Start, bit4=Up, bit5=Down, bit6=Left, bit7=Right）
			for (int b = 0; b < 8; b++)
			{
				console.Bus.Controller1.SetButton((NesButton)b, (buttons & (1 << b)) != 0);
			}
		}

		for (int i = 0; i < _scriptedPresses.Count; i++)
		{
			(NesButton button, int downFrame, int upFrame) = _scriptedPresses[i];
			if (frame >= downFrame && frame < upFrame)
			{
				console.Bus.Controller1.SetButton(button, true);
			}
		}
	}

	/// <summary>
	/// 把 APU 产出的样本收集到内存里，供 <c>--dump-audio</c> 导出。
	/// 做音频测试时用它顶替播放器，这样无头也能拿到确定的样本序列。
	/// </summary>
	private sealed class AudioCollector : IAudioSink
	{
		private readonly List<short> _samples = new();

		public int SampleRate => Apu.SampleRate;

		public void Write(ReadOnlySpan<float> samples)
		{
			foreach (float sample in samples)
			{
				_samples.Add((short)(Math.Clamp(sample, -1f, 1f) * 32767f));
			}
		}

		public short[] ToArray() => _samples.ToArray();
	}
}
