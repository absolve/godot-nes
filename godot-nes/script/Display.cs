using Godot;
using GodotNes.Core;

namespace GodotNes.Game;

/// <summary>
/// 把模拟器的 256x240 帧缓冲画到屏幕上。
/// 每帧复用同一个 Image（SetData）+ 同一个 ImageTexture（Update），不做额外分配；
/// 采样固定最近邻、按比例整数缩放居中，保证像素不糊也不变形。
/// </summary>
public partial class Display : TextureRect
{
	/// <summary>清屏被调用过几次（测试用：关闭 ROM 必须走到这里）。</summary>
	public int ClearCount { get; private set; }

	private Image? _image;
	private ImageTexture? _texture;

	public override void _Ready()
	{
		TextureFilter = TextureFilterEnum.Nearest;
		ExpandMode = ExpandModeEnum.IgnoreSize;
		StretchMode = StretchModeEnum.KeepAspectCentered;

		var initial = new byte[Ppu.ScreenWidth * Ppu.ScreenHeight * 4];
		_image = Image.CreateFromData(Ppu.ScreenWidth, Ppu.ScreenHeight, false, Image.Format.Rgba8, initial);
		_texture = ImageTexture.CreateFromImage(_image);
		Texture = _texture;
	}

	/// <summary>把一帧 RGBA8 数据推上屏（长度必须是 256 * 240 * 4）。</summary>
	public void Present(byte[] frame)
	{
		if (_image is null || _texture is null || frame is null)
		{
			return;
		}

		_image.SetData(Ppu.ScreenWidth, Ppu.ScreenHeight, false, Image.Format.Rgba8, frame);
		_texture.Update(_image);
	}

	/// <summary>
	/// 清空画面（关闭 ROM 时用）。不清的话，卸载卡带之后上一帧会一直留在屏幕上，
	/// 看上去像"游戏还在暂停着"，容易让人以为还装着 ROM。
	/// 清成和主题底色一致的深灰，而不是纯黑 —— 和主界面的背景接得上。
	/// </summary>
	public void Clear()
	{
		if (_image is null || _texture is null)
		{
			return;
		}

		_image.Fill(BackdropColor);
		ClearCount++;
		_texture.Update(_image);
	}

	/// <summary>清屏色：和 theme/dark_theme.tres 里的底色一致。</summary>
	private static readonly Color BackdropColor = new(0x20 / 255f, 0x21 / 255f, 0x24 / 255f);
}
