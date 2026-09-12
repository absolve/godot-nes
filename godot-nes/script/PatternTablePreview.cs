using Godot;
using GodotNes.Core;

namespace GodotNes.Game;

/// <summary>
/// PPU 调试窗口里的 pattern table 预览。
///
/// 故意做成 C# 节点：预视图的数据来自卡带 CHR，属于"重数据"；
/// 放在 C# 侧解码就不用把字节数组跨语言传来传去。
/// GDScript 只负责摆放它，然后调用无参的 <see cref="Refresh"/>。
/// </summary>
public partial class PatternTablePreview : TextureRect
{
	public const int PreviewWidth = 128;
	public const int PreviewHeight = 128;

	private const int TileSize = 8;
	private const int TilesPerRow = 16;
	private const int TileCount = 256;

	/// <summary>灰色 4 级，对应调色板 $0F/$00/$10/$30。</summary>
	private static readonly int[] GrayscaleRamp = { 0x0F, 0x00, 0x10, 0x30 };

	private readonly byte[] _pixels = new byte[PreviewWidth * PreviewHeight * 4];
	private Image? _image;
	private ImageTexture? _texture;

	/// <summary>0 = $0000 表，1 = $1000 表。</summary>
	[Export]
	public int TableIndex { get; set; }

	public override void _Ready()
	{
		TextureFilter = TextureFilterEnum.Nearest;
		ExpandMode = ExpandModeEnum.IgnoreSize;
		StretchMode = StretchModeEnum.KeepAspect;
		CustomMinimumSize = new Vector2(PreviewWidth, PreviewHeight);

		_image = Image.CreateFromData(PreviewWidth, PreviewHeight, false, Image.Format.Rgba8, _pixels);
		_texture = ImageTexture.CreateFromImage(_image);
		Texture = _texture;

		Refresh();
	}

	/// <summary>重新从卡带 CHR 解码一份预览。没装卡带时画成空的。</summary>
	public void Refresh()
	{
		if (_image is null || _texture is null)
		{
			return;
		}

		Mapper? mapper = EmulatorService.Instance?.Console.Ppu.Mapper;
		if (mapper is null)
		{
			FillBackground();
		}
		else
		{
			DecodeTable(mapper);
		}

		_image.SetData(PreviewWidth, PreviewHeight, false, Image.Format.Rgba8, _pixels);
		_texture.Update(_image);
	}

	private void DecodeTable(Mapper mapper)
	{
		int baseAddress = TableIndex * 0x1000;

		for (int tile = 0; tile < TileCount; tile++)
		{
			int tileX = (tile % TilesPerRow) * TileSize;
			int tileY = (tile / TilesPerRow) * TileSize;
			int tileAddress = baseAddress + (tile * 16);

			for (int row = 0; row < TileSize; row++)
			{
				byte low = mapper.PpuRead((ushort)(tileAddress + row));
				byte high = mapper.PpuRead((ushort)(tileAddress + row + 8));
				int rowOffset = ((tileY + row) * PreviewWidth) + tileX;

				for (int column = 0; column < TileSize; column++)
				{
					int bit = 7 - column;
					int value = ((low >> bit) & 1) | (((high >> bit) & 1) << 1);
					NesPalette.WritePixel(_pixels, (rowOffset + column) * 4, GrayscaleRamp[value]);
				}
			}
		}
	}

	private void FillBackground()
	{
		uint color = NesPalette.ToPackedRgba(0x0F);
		byte r = (byte)(color >> 24);
		byte g = (byte)(color >> 16);
		byte b = (byte)(color >> 8);
		byte a = (byte)color;

		for (int offset = 0; offset < _pixels.Length; offset += 4)
		{
			_pixels[offset + 0] = r;
			_pixels[offset + 1] = g;
			_pixels[offset + 2] = b;
			_pixels[offset + 3] = a;
		}
	}
}
