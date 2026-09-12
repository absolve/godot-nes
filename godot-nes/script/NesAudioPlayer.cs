using System;
using Godot;
using GodotNes.Core;

namespace GodotNes.Game;

/// <summary>
/// APU 音频输出。用 AudioStreamGenerator 做低延迟流式播放：
/// APU 产出单声道样本，这里复制成立体声推进播放缓冲。
/// 阶段 0 的 APU 还是桩（静音），节点先把链路接好，阶段 6 接上混音即可出声。
/// </summary>
public partial class NesAudioPlayer : AudioStreamPlayer, IAudioSink
{
	[Export]
	public float MixRate { get; set; } = 44100f;

	[Export]
	public float BufferLength { get; set; } = 0.25f;

	private AudioStreamGeneratorPlayback? _playback;
	private Vector2[] _stereo = Array.Empty<Vector2>();

	/// <summary>因为播放缓冲满而被丢掉的样本数（正常应该是 0；一直涨就说明喂得太快）。</summary>
	public long DroppedSamples { get; private set; }

	public int SampleRate => (int)MixRate;

	public override void _Ready()
	{
		Stream = new AudioStreamGenerator
		{
			MixRate = MixRate,
			BufferLength = BufferLength,
		};

		Play();
		_playback = GetStreamPlayback() as AudioStreamGeneratorPlayback;

		if (_playback is null)
		{
			GD.PushWarning("[godot-nes] AudioStreamGenerator 播放实例创建失败，本次运行没有声音。");
		}
	}

	public override void _ExitTree()
	{
		// 退出时停流并松开播放实例的引用。
		// 注意：只在无头（--headless，音频驱动是 Dummy）运行时，Godot 退出时会打印一条
		//   WARNING: 1 ObjectDB instance was leaked at exit
		// --verbose 显示是 AudioStreamGeneratorPlayback，属于 Godot 内部对 audio playback
		// 的引用计数，Stop()/Dispose() 都消不掉；带窗口正常运行不会出现，也不影响功能。
		Stop();
		_playback = null;
	}

	/// <summary>
	/// 推一批单声道样本（[-1, 1]）。
	/// 缓冲装不下就只推装得下的那部分：整批丢弃会在听感上变成"咔"的一下，
	/// 而按正确速度喂（见 EmulatorCore 的帧节流）本来就不该装不下 —— 真丢了多少记在
	/// <see cref="DroppedSamples"/> 里，方便排查。
	/// </summary>
	public void Write(ReadOnlySpan<float> samples)
	{
		if (_playback is null || samples.Length == 0)
		{
			return;
		}

		int room = _playback.GetFramesAvailable();
		if (room <= 0)
		{
			DroppedSamples += samples.Length;
			return;
		}

		int count = Math.Min(room, samples.Length);
		if (count < samples.Length)
		{
			DroppedSamples += samples.Length - count;
		}

		if (_stereo.Length != count)
		{
			_stereo = new Vector2[count];
		}

		for (int i = 0; i < count; i++)
		{
			float sample = samples[i];
			_stereo[i] = new Vector2(sample, sample);
		}

		_playback.PushBuffer(_stereo);
	}
}
