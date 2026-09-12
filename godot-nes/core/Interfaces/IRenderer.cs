namespace GodotNes.Core;

/// <summary>
/// 帧缓冲提供者（目前由 <see cref="Ppu"/> 实现）。
/// 缓冲区是 RGBA8、行优先、从上到下，长度必须是 Width * Height * 4，
/// 这样 Godot 侧可以直接喂给 Image.SetData 而不用做任何转换。
/// </summary>
public interface IRenderer
{
	int Width { get; }

	int Height { get; }

	/// <summary>RGBA8 帧缓冲，长度 = Width * Height * 4。</summary>
	byte[] FrameBuffer { get; }

	/// <summary>取走"本帧已画完"标志（读后清零），用来跳过重复的纹理上传。</summary>
	bool TakeFrameReady();
}
