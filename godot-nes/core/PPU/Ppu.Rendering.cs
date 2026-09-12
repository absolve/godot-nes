namespace GodotNes.Core;

/// <summary>
/// PPU 的渲染部分（Ppu 的另一半，见 Ppu.cs）。
///
/// 每条可见扫描线进来一次：先铺背景，再叠精灵。
/// 背景直接用滚动寄存器算出"这一行要看名称表的哪一行"，不需要移位寄存器。
/// </summary>
public sealed partial class Ppu
{
    /// <summary>当前扫描线上每个像素的背景是否不透明 —— 精灵优先级和精灵 0 命都要用它。</summary>
    private readonly bool[] _backgroundOpaque = new bool[ScreenWidth];

    private void RenderScanline(int y)
    {
        if ((Mask & 0x08) != 0)
        {
            RenderBackground(y);
        }
        else
        {
            FillWithBackdrop(y);
        }

        if ((Mask & 0x10) != 0)
        {
            RenderSprites(y);
        }
    }

    /// <summary>背景关闭时整行输出背景色（调色板 $3F00）。</summary>
    private void FillWithBackdrop(int y)
    {
        int offset = y * ScreenWidth;
        byte backdrop = (byte)(_paletteRam[0] & 0x3F);

        for (int x = 0; x < ScreenWidth; x++)
        {
            IndexBuffer[offset + x] = backdrop;
            _backgroundOpaque[x] = false;
        }
    }

    private void RenderBackground(int y)
    {
        int offset = y * ScreenWidth;
        byte backdrop = (byte)(_paletteRam[0] & 0x3F);
        bool showLeftColumn = (Mask & 0x02) != 0;
        int patternBase = BackgroundPatternBase;

        // 横向：世界坐标 = 精细 X + 粗 X * 8 + 屏幕 x
        int scrollX = _fineXScroll + ((_tempAddress & 0x1F) << 3);
        int nametableBase = (_tempAddress >> 10) & 0x03;

        // 纵向：世界坐标 = 精细 Y + 粗 Y * 8 + （当前扫描线 - 基准行）。
        // 基准行见 Ppu._scrollBaseLine：
        //   纵向写非 0（场景分屏 $2005 = X,$22）→ 基准 = 写入行，
        //   纵向写 0（底栏分屏 $2005 = $00,$00）→ 基准 = 0（从名称表顶行算）。
        // 这两条都是实测出来的，且各有一个客观验证：
        //   - 场景：帧 1600 与参照实现逐像素 0 差异；
        //   - 底栏：LEVEL/EXP 那行（名称表 26）落在屏幕 208-215，与 FCEUX 一致。
        int worldY = ((_tempAddress >> 12) & 0x07) + (((_tempAddress >> 5) & 0x1F) << 3)
            + (y - _scrollBaseLine);
        int nametableY = worldY >= 240 ? 1 : 0;          // 越过 240 行换到下面那块名称表
        int rowInTable = worldY - (nametableY * 240);
        int coarseRow = rowInTable >> 3;
        int fineY = rowInTable & 0x07;

        // 属性字节里装着 4 个 2 位字段：bit0-1 = 左上、bit2-3 = 右上、bit4-5 = 左下、bit6-7 = 右下。
        // 行决定"上/下"（相差 4 位），列决定"左/右"（相差 2 位）。(coarseRow & 2) 得到 2，
        // 要再左移 1 位才是 4；列的那 2 位本身就是移位量，不能再移 —— 这里错过一次，见变更记录。
        int attributeRowShift = (coarseRow & 0x02) << 1;
        int nametableRowBase = coarseRow << 5;
        int attributeRowBase = (coarseRow >> 2) << 3;

        // 按"世界图块"循环，而不是逐像素：一个 8 像素的世界图块里图块号/属性/调色板/图案字节
        // 都是同一份，所以每块只取一次 VRAM，再把这 8 个像素落到屏幕上（落到屏幕外的丢掉）。
        //
        // 为什么必须按世界图块（而不是按屏幕上的 8 像素）分块：横向滚动不是 8 的整数倍，
        // 屏幕上的一个 8 像素格子会横跨两个世界图块，按屏幕分块就会画错。
        //
        // 这一改很值：原来是逐像素 4 次 ReadVram（一帧 24 万次，其中一半要进 mapper 的虚函数），
        // 实测一帧 14ms —— 换成按图块之后 VRAM 读取少 8 倍。
        int firstWorldTile = scrollX >> 3;
        int lastWorldTile = (scrollX + ScreenWidth - 1) >> 3;

        for (int worldTile = firstWorldTile; worldTile <= lastWorldTile; worldTile++)
        {
            int worldTileX = worldTile << 3;
            int nametableX = worldTileX >= 256 ? 1 : 0;   // 越过 256 列换到右边那块名称表
            int coarseCol = (worldTileX - (nametableX * 256)) >> 3;

            // 名称表编号：基址（来自滚动）异或上越界翻转的那一位
            int nametable = nametableBase ^ nametableX ^ (nametableY << 1);
            int nametableAddress = 0x2000 + (nametable << 10);

            int tile = ReadVram((ushort)(nametableAddress + nametableRowBase + coarseCol));

            // 属性表：一个字节管 4x4 个图块，行方向每 4 行一个字节，列方向每 4 列一个字节
            byte attribute = ReadVram((ushort)(nametableAddress + 0x3C0
                + attributeRowBase + (coarseCol >> 2)));
            int palette = (attribute >> (attributeRowShift + (coarseCol & 0x02))) & 0x03;

            int patternAddress = patternBase + (tile << 4) + fineY;
            byte low = ReadVram((ushort)patternAddress);
            byte high = ReadVram((ushort)(patternAddress + 8));


            int x = worldTileX - scrollX;
            for (int fineX = 0; fineX < 8; fineX++, x++)
            {
                if (x < 0)
                {
                    continue;                                // 图块左半截在屏幕外
                }

                if (x >= ScreenWidth)
                {
                    break;                                   // 图块右半截在屏幕外
                }

                if (x < 8 && !showLeftColumn)
                {
                    IndexBuffer[offset + x] = backdrop;
                    _backgroundOpaque[x] = false;
                    continue;
                }

                int bit = 7 - fineX;
                int colorBit = ((low >> bit) & 1) | (((high >> bit) & 1) << 1);

                if (colorBit == 0)
                {
                    IndexBuffer[offset + x] = backdrop;      // 图案位为 0 就是透明，露出背景色
                    _backgroundOpaque[x] = false;
                }
                else
                {
                    IndexBuffer[offset + x] = (byte)(_paletteRam[(palette << 2) + colorBit] & 0x3F);
                    _backgroundOpaque[x] = true;
                }
            }
        }
    }

    private void RenderSprites(int y)
    {
        int offset = y * ScreenWidth;
        bool showLeftColumn = (Mask & 0x04) != 0;
        bool size8x16 = SpriteSize8x16;
        int spriteHeight = size8x16 ? 16 : 8;
        int patternBase = size8x16 ? 0 : SpritePatternBase;
        int rendered = 0;
        int evaluated = 0;
        bool limitHit = false;

        for (int i = 0; i < OamEntryCount; i++)
        {
            // OAM 里的 Y 比屏幕坐标小 1
            int spriteY = _oam[(i * 4) + 0] + 1;
            int row = y - spriteY;
            if (row < 0 || row >= spriteHeight)
            {
                continue;
            }

            evaluated++;
            rendered++;
            if (rendered > 8)
            {
                limitHit = true;
                Status |= 0x20;                          // 精灵溢出：一条扫描线最多 8 个精灵
                break;
            }

            int tile = _oam[(i * 4) + 1];
            byte attributes = _oam[(i * 4) + 2];
            int spriteX = _oam[(i * 4) + 3];

            bool flipH = (attributes & 0x40) != 0;
            bool flipV = (attributes & 0x80) != 0;
            bool behindBackground = (attributes & 0x20) != 0;
            int spritePalette = attributes & 0x03;

            int pixelRow = flipV ? spriteHeight - 1 - row : row;

            int tileBase = patternBase;
            int tileIndex = tile;
            if (size8x16)
            {
                tileBase = (tile & 0x01) << 12;          // 8x16 时图块号的 bit0 选图案表
                tileIndex = (tile & 0xFE) + (pixelRow >= 8 ? 1 : 0);
                pixelRow &= 0x07;
            }

            int patternAddress = tileBase + (tileIndex << 4) + pixelRow;
            byte low = ReadVram((ushort)patternAddress);
            byte high = ReadVram((ushort)(patternAddress + 8));

            for (int px = 0; px < 8; px++)
            {
                int x = spriteX + px;
                if (x >= ScreenWidth)
                {
                    break;
                }

                if (x < 8 && !showLeftColumn)
                {
                    continue;
                }

                int bit = flipH ? px : 7 - px;
                int colorBit = ((low >> bit) & 1) | (((high >> bit) & 1) << 1);
                if (colorBit == 0)
                {
                    continue;                            // 透明像素不画
                }

                // 精灵 0 命中：**不管优先级**，只要精灵 0 的不透明像素压在背景的不透明像素上就置位
                // （最右一列 x = 255 不算；左 8 像素算不算由"背景/精灵有没有遮住左列"决定，
                // 这一点已经体现在 _backgroundOpaque 里了，所以这里不再单独判 x）。
                //
                // 这一条踩了个大坑：SMB 的标题画面在 $8150 死等这个标志
                // （`LDA $2002 / AND #$40 / BEQ $8150`），而它的精灵 0 正好带"在背景后面"的属性。
                // 先判优先级、命中又放在 continue 之后的话，标志永远不置位 —— 游戏卡死在这个循环里，
                // 表现就是"马里奥的精灵不出现、也按不了开始"。
                if (i == 0 && x < 255 && _backgroundOpaque[x])
                {
                    Status |= 0x40;
                }

                if (behindBackground && _backgroundOpaque[x])
                {
                    continue;                            // 优先级低，而且背景在这里不透明
                }

                IndexBuffer[offset + x] =
                    (byte)(_paletteRam[0x10 + (spritePalette << 2) + colorBit] & 0x3F);
            }
        }

        SpriteLineLogger?.Invoke(y, evaluated, rendered, limitHit);
    }
}
