using System;

namespace GodotNes.Core;

/// <summary>
/// APU 的输出目标。Godot 侧由 <c>NesAudioPlayer</c>（AudioStreamGenerator）实现。
/// </summary>
public interface IAudioSink
{
	/// <summary>期望采样率（Hz）。</summary>
	int SampleRate { get; }

	/// <summary>单声道 float 样本，取值范围 [-1, 1]。</summary>
	void Write(ReadOnlySpan<float> samples);
}
