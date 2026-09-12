using System;

namespace GodotNes.Core;

/// <summary>
/// 2C02 PPU。
///
/// **设计取舍：按扫描线渲染，不做 dot 级流水线。**
/// 真机是每个 dot 取一次图块、用移位寄存器吐像素，要完整模拟 Loopy 寄存器和取指时序，
/// 代码量翻好几倍。这里改成"进入一条可见扫描线时，用当前的滚动寄存器一次性算完这一行"。
///
/// 代价（写在明处，免得以后当成 bug）：
///   - 同一扫描线**行内**改滚动寄存器（$2005/$2006/$2000）不会立刻生效，只影响下一行；
///   - 精灵 0 命中、精灵溢出的时机是行级的，不是点级的；
///   - 没有模拟 v 寄存器在行内自增，帧末也不会把 t 拷回 v。
/// 对绝大多数游戏够用（含《超级马里奥兄弟》那种靠精灵 0 命中分屏的状态栏）；做彩虹效应、
/// 曼哈顿计划那类靠行内时序的 demo 就得换成 dot 级。
///
/// 调色板是 6 位索引。渲染时先写进 <see cref="IndexBuffer"/>，进入 VBlank 时统一转成
/// RGBA 写进 <see cref="FrameBuffer"/> —— 输出格式和硬件一致，跟参照实现比对也方便。
/// </summary>
public sealed partial class Ppu : IRenderer
{
    public const int ScreenWidth = 256;
    public const int ScreenHeight = 240;

    /// <summary>一帧 262 条扫描线（240 可见 + 1 条 pre-render + VBlank）。</summary>
    public const int ScanlineCount = 262;

    /// <summary>每条扫描线 341 个 dot。</summary>
    public const int DotsPerScanline = 341;

    /// <summary>一帧的 PPU 周期数（= 262 × 341），正好是 CPU 周期的 3 倍。</summary>
    public const int CyclesPerFrame = ScanlineCount * DotsPerScanline;

    public const int OamEntryCount = 64;
    public const int OamSize = OamEntryCount * 4;
    public const int PaletteSize = 32;
    public const int NametableSize = 0x400;
    public const int NametableCount = 4;

    private readonly byte[] _vram = new byte[NametableSize * NametableCount];
    private readonly byte[] _paletteRam = new byte[PaletteSize];
    private readonly byte[] _oam = new byte[OamSize];

    // 滚动与地址：v = 当前 VRAM 地址，t = 临时地址（滚动就存在这里），x = 精细 X 滚动
    private ushort _vramAddress;
    private ushort _tempAddress;
    private byte _fineXScroll;
    private bool _writeToggle;
    private byte _readBuffer;

    /// <summary>
    /// 当前纵向卷轴是在哪条扫描线上被重设的（$2005 第二次写 / $2006 第二次写）。
    /// 硬件上 v 每行自增，所以纵向要从"重设那一行"开始算，不能一律加扫描线号 ——
    /// 见 <see cref="RenderBackground"/> 里 worldY 的说明（Mighty Final Fight 踩过）。
    /// VBlank 里重设的按 0 处理（pre-render 会把 t 拷进 v，等价于从第 0 行开始）。
    /// </summary>
    private int _scrollBaseLine;

    /// <summary>PPU 的 I/O 锁存：最后一次写进 PPU 寄存器的值，$2002 的低 5 位读的就是它。</summary>
    private byte _ioLatch;

    /// <summary>
    /// 寄存器写日志钩子（调试用，默认 null）：参数是 (寄存器, 值, 当前扫描线)。
    /// 排查"分屏时切图案表/卷轴"这类问题时，靠它看清游戏到底在哪条扫描线写了什么。
    /// </summary>
    public Action<ushort, byte, int>? RegisterWriteLogger { get; set; }

    /// <summary>每条可见扫描线开始时回调一次（调试用，默认 null）：参数是扫描线号。</summary>
    public Action<int>? ScanlineLogger { get; set; }

    /// <summary>精细 X 卷轴（调试用）。</summary>
    public int DbgFineXScroll => _fineXScroll;

    /// <summary>t 寄存器（调试用）：滚动/名称表都存这里。</summary>
    public int DbgTempAddress => _tempAddress;

    /// <summary>纵向卷轴基准行（调试用）：见 RenderBackground 里的说明。</summary>
    /// <summary>纵向卷轴基准行（调试用）。</summary>
    public int DbgScrollBaseLine => _scrollBaseLine;

    /// <summary>当前扫描线（调试用）。</summary>
    public int DbgScanline => _scanline;

    /// <summary>
    /// 每条可见扫描线的精灵评估结果（调试用，默认 null）：
    /// (扫描线, 匹配上这行的精灵数, 实际画出来的数量, 是否撞上"每行 8 个"上限)。
    /// 排查"某一段画面上的精灵整体不显示"时就看这条。
    /// </summary>
    public Action<int, int, int, bool>? SpriteLineLogger { get; set; }

    /// <summary>OAM 原始数据（调试用，64 项 × 4 字节）。</summary>
    public byte[] DbgOam => _oam;

    /// <summary>调色板 RAM（调试用，32 字节）。</summary>
    public byte[] DbgPaletteRam => _paletteRam;

    /// <summary>临时排查开关：打开后在底栏某一行记录每个图块的取图信息。</summary>
    public bool DbgBackgroundProbe { get; set; }

    /// <summary>探针收集到的行。</summary>
    public System.Collections.Generic.List<string> DbgBackgroundLines { get; } = new();

    private int _scanline;
    private bool _nmiRequested;

    /// <summary>$2000 PPUCTRL。</summary>
    public byte Ctrl { get; set; }

    /// <summary>$2001 PPUMASK。</summary>
    public byte Mask { get; set; }

    /// <summary>$2002 PPUSTATUS（bit7 = VBlank，bit6 = 精灵 0 命中，bit5 = 精灵溢出）。</summary>
    public byte Status { get; set; }

    /// <summary>$2003 OAMADDR。</summary>
    public byte OamAddr { get; set; }

    /// <summary>$2004 OAMDATA。写入时会像真机一样让 OAMADDR 递增。</summary>
    public byte OamData
    {
        get => _oam[OamAddr];
        set
        {
            _oam[OamAddr] = value;
            OamAddr++;
        }
    }

    /// <summary>
    /// 名称表镜像。装载卡带时由 <see cref="NesConsole"/> 按卡带头设成固定值；
    /// MMC1 / MMC3 / AxROM 这类能改镜像的 mapper 会盖住它（见 <see cref="EffectiveMirroring"/>）。
    /// </summary>
    public Mirroring Mirroring { get; set; } = Mirroring.Horizontal;

    /// <summary>
    /// 当前真正生效的镜像：mapper 说了算就用 mapper 的，否则用卡带头里那个。
    /// 渲染取名称表、以及调试窗口显示都走这个。
    /// </summary>
    public Mirroring EffectiveMirroring => Mapper?.DynamicMirroring ?? Mirroring;

    /// <summary>卡带映射器，负责 pattern table（CHR）读写。</summary>
    public Mapper? Mapper { get; set; }

    public byte[] Oam => _oam;

    public byte[] Vram => _vram;

    public byte[] PaletteRam => _paletteRam;

    /// <summary>当前 VRAM 地址（调试显示用）。</summary>
    public ushort VramAddress => _vramAddress;

    /// <summary>当前扫描线（0-261，调试显示用）。</summary>
    public int Scanline => _scanline;

    /// <summary>当前扫描线内的 dot（0-340）。</summary>
    public int Dot { get; private set; }

    /// <summary>已经渲染完的帧数。</summary>
    public long FrameCount { get; private set; }

    public bool NmiEnabled => (Ctrl & 0x80) != 0;

    public bool RenderingEnabled => (Mask & 0x18) != 0;

    public bool SpriteSize8x16 => (Ctrl & 0x20) != 0;

    /// <summary>背景 pattern table 基址：$0000 或 $1000。</summary>
    public int BackgroundPatternBase => (Ctrl & 0x10) != 0 ? 0x1000 : 0x0000;

    /// <summary>精灵 pattern table 基址（8x16 模式下该位被忽略）。</summary>
    public int SpritePatternBase => (Ctrl & 0x08) != 0 ? 0x1000 : 0x0000;

    /// <summary>$2007 读写后 VRAM 地址的增量（1 或 32）。</summary>
    public int Increment => (Ctrl & 0x04) != 0 ? 32 : 1;

    public int Width => ScreenWidth;

    public int Height => ScreenHeight;

    /// <summary>6 位调色板索引，256x240。渲染的中间结果，也是和参照实现比对的依据。</summary>
    public byte[] IndexBuffer { get; } = new byte[ScreenWidth * ScreenHeight];

    /// <summary>RGBA8 帧缓冲，长度 256 * 240 * 4，可以直接交给 Image.SetData。</summary>
    public byte[] FrameBuffer { get; } = new byte[ScreenWidth * ScreenHeight * 4];

    public bool FrameReady { get; private set; }

    public bool TakeFrameReady()
    {
        bool ready = FrameReady;
        FrameReady = false;
        return ready;
    }

    public void MarkFrameReady() => FrameReady = true;

    // ------------------------------------------------------------------ 复位与时序

    /// <summary>复位：寄存器清零、扫描线回到 0、VRAM/OAM/调色板清空（真机是不确定的，这里取确定性）。</summary>
    public void Reset()
    {
        Ctrl = 0;
        Mask = 0;
        Status = 0;
        OamAddr = 0;
        _vramAddress = 0;
        _tempAddress = 0;
        _fineXScroll = 0;
        _writeToggle = false;
        _readBuffer = 0;
        _scanline = 0;
        Dot = 0;
        _nmiRequested = false;
        FrameReady = false;
        FrameCount = 0;

        Array.Clear(_vram, 0, _vram.Length);
        Array.Clear(_paletteRam, 0, _paletteRam.Length);
        Array.Clear(_oam, 0, _oam.Length);
        Array.Clear(IndexBuffer, 0, IndexBuffer.Length);

        BeginScanline(0);
    }

    /// <summary>
    /// 推进 <paramref name="ppuCycles"/> 个 PPU 周期。事件只在扫描线边界上处理，
    /// 所以这里是个"跨过几条扫描线"的循环，而不是一个 dot 一个 dot 地转。
    /// </summary>
    public void Step(int ppuCycles)
    {
        Dot += ppuCycles;

        while (Dot >= DotsPerScanline)
        {
            Dot -= DotsPerScanline;
            _scanline++;

            if (_scanline >= ScanlineCount)
            {
                _scanline = 0;
                FrameCount++;
            }

            BeginScanline(_scanline);
        }
    }


    /// <summary>见 <see cref="_scrollBaseLine"/>：只有纵向值非 0 的写入才更新基准。</summary>
    private void LatchScrollBase()
    {
        int coarseY = (_tempAddress >> 5) & 0x1F;
        int fineY = (_tempAddress >> 12) & 0x07;
        if (coarseY == 0 && fineY == 0)
        {
            // 纵向写 0：当作"从名称表顶行开始"，基准归零。
            // 底栏分屏就是这种写（$2005 = $00,$00），归零后 LEVEL/EXP 那行（名称表 26）
            // 正好落在屏幕 208-215 行，与 FCEUX 一致。
            _scrollBaseLine = 0;
            return;
        }

        _scrollBaseLine = _scanline < ScreenHeight ? _scanline + 1 : 0;
    }

    private void BeginScanline(int scanline)
    {
        if (scanline < ScreenHeight)
        {
            ScanlineLogger?.Invoke(scanline);

            RenderScanline(scanline);

            // 渲染开着的时候，真机每扫描线会有一次 A12 上升沿（取精灵图案），
            // MMC3 的扫描线计数器就是数这个的。渲染关掉就没有上升沿，计数器也不动 ——
            // 所以这里跟着 Mask 走，而不是无条件每行一次。
            if (RenderingEnabled)
            {
                Mapper?.OnScanline();
            }

            return;
        }

        if (scanline == 241)
        {
            // VBlank 开始：这一帧的画面已经完整了，先把索引转成 RGBA，再置标志、发 NMI
            ConvertToRgba();
            Status |= 0x80;
            if (NmiEnabled)
            {
                _nmiRequested = true;
            }

            MarkFrameReady();
            return;
        }

        if (scanline == 261)
        {
            // pre-render：清掉 VBlank / 精灵 0 命中 / 精灵溢出
            Status = (byte)(Status & 0x1F);

            _scrollBaseLine = 0;


            // pre-render 这一行也会去取精灵图案，所以 A12 同样有一个上升沿 ——
            // 一帧一共 **241** 次，不是 240 次。差这一次会让 MMC3 的扫描线中断
            // 整体错开一条扫描线；像《Mighty Final Fight》这种一帧里要重新装两次
            // 计数器来分屏的游戏，错开之后分屏点会跑到画面外，整个游戏就卡在
            // 等中断的循环里（画面停在半成品状态）。
            if (RenderingEnabled)
            {
                Mapper?.OnScanline();
            }
        }
    }

    /// <summary>取走"要发 NMI"的请求（读后清零）。CPU 在每条指令之前问一次。</summary>
    public bool TakeNmiRequest()
    {
        bool requested = _nmiRequested;
        _nmiRequested = false;
        return requested;
    }

    private void ConvertToRgba()
    {
        // PPUMASK bit0 是灰度：真机是把每个通道的低位砍掉，效果上就等于落到调色板的灰度行
        bool grayscale = (Mask & 0x01) != 0;

        for (int i = 0; i < IndexBuffer.Length; i++)
        {
            int colorIndex = IndexBuffer[i] & 0x3F;
            if (grayscale)
            {
                colorIndex &= 0x30;
            }

            NesPalette.WritePixel(FrameBuffer, i * 4, colorIndex);
        }
    }

    // ------------------------------------------------------------------ 寄存器

    public byte ReadRegister(ushort register)
    {
        switch (register & 0x0007)
        {
            case 0x02:   // PPUSTATUS
            {
                // 高 3 位是状态，低 5 位是**开放总线**：真机上返回 PPU 数据总线上残留的值，
                // 也就是"最后一次写进 PPU 寄存器的值"的低 5 位。
                // 这一条不是可有可无的：SMB 开机那段 `LDA $2002 / BPL` 等 VBlank 的循环里，
                // 低 5 位就是之前 `STA $2000` 写进去的 $10；少了它，A 和 Z 标志就和真机不一样，
                // 冒烟测试里的 CPU trace 差分第 8 行就会分叉。
                byte value = (byte)((Status & 0xE0) | (_ioLatch & 0x1F));
                Status = (byte)(Status & ~0x80);   // 读 $2002 会清 VBlank 标志
                _writeToggle = false;              // 同时复位 $2005/$2006 的锁存
                return value;
            }

            case 0x04:   // OAMDATA
                return _oam[OamAddr];

            case 0x07:   // PPUDATA
            {
                byte value = _readBuffer;
                _readBuffer = ReadVram(_vramAddress);
                if ((_vramAddress & 0x3FFF) >= 0x3F00)
                {
                    // 读调色板不走缓冲，直接返回
                    value = _readBuffer;
                }

                _vramAddress = (ushort)((_vramAddress + Increment) & 0x7FFF);
                return value;
            }

            default:
                return 0x00;
        }
    }

    public void WriteRegister(ushort register, byte value)
    {
        // PPU 的 I/O 锁存：$2002 读回去的低 5 位就是它（开放总线）
        _ioLatch = value;

        RegisterWriteLogger?.Invoke(register, value, _scanline);

        switch (register & 0x0007)
        {
            case 0x00:   // PPUCTRL
                Ctrl = value;
                // 真机写 $2000 会把低 2 位的名称表选择拷贝进 t 的 bit10-11 —— 游戏靠这个切换名称表，
                // 少了这一句，"滚动到另一块名称表"就会失效
                _tempAddress = (ushort)((_tempAddress & 0x73FF) | ((value & 0x03) << 10));
                break;

            case 0x01:   // PPUMASK
                Mask = value;
                break;

            case 0x03:   // OAMADDR
                OamAddr = value;
                break;

            case 0x04:   // OAMDATA
                OamData = value;
                break;

            case 0x05:   // PPUSCROLL：两次写入拼出 t 的水平 / 垂直部分
                if (!_writeToggle)
                {
                    _fineXScroll = (byte)(value & 0x07);
                    _tempAddress = (ushort)((_tempAddress & 0x7FE0) | (value >> 3));
                }
                else
                {
                    _tempAddress = (ushort)((_tempAddress & 0x0C1F)
                        | ((value & 0x07) << 12)
                        | ((value & 0xF8) << 2));
                    LatchScrollBase();
                }

                _writeToggle = !_writeToggle;
                break;

            case 0x06:   // PPUADDR
                if (!_writeToggle)
                {
                    _tempAddress = (ushort)((_tempAddress & 0x00FF) | ((value & 0x3F) << 8));
                }
                else
                {
                    _tempAddress = (ushort)((_tempAddress & 0xFF00) | value);
                    _vramAddress = _tempAddress;
                    LatchScrollBase();
                }

                _writeToggle = !_writeToggle;
                break;

            case 0x07:   // PPUDATA
                WriteVram(_vramAddress, value);
                _vramAddress = (ushort)((_vramAddress + Increment) & 0x7FFF);
                break;
        }
    }

    /// <summary>PPU 地址空间读（$0000-$3FFF）。</summary>
    public byte ReadVram(ushort address)
    {
        address &= 0x3FFF;
        if (address < 0x2000)
        {
            return Mapper?.PpuRead(address) ?? 0x00;
        }

        if (address < 0x3F00)
        {
            return _vram[GetNametableOffset(address)];
        }

        return _paletteRam[GetPaletteOffset(address)];
    }

    /// <summary>PPU 地址空间写（$0000-$3FFF）。</summary>
    public void WriteVram(ushort address, byte value)
    {
        address &= 0x3FFF;
        if (address < 0x2000)
        {
            Mapper?.PpuWrite(address, value);
            return;
        }

        if (address < 0x3F00)
        {
            _vram[GetNametableOffset(address)] = value;
            return;
        }

        _paletteRam[GetPaletteOffset(address)] = value;
    }

    /// <summary>$4014 OAM DMA 的落地部分：从 OAMADDR 开始写 256 字节，写满一圈回绕。</summary>
    public void WriteOamDma(ReadOnlySpan<byte> pageData)
    {
        int address = OamAddr;
        for (int i = 0; i < pageData.Length && i < OamSize; i++)
        {
            _oam[address & 0xFF] = pageData[i];
            address++;
        }
    }

    /// <summary>$2000-$3EFF 映射到 4 块 1KB 名称表的哪一块（含 $3000 区的镜像）。</summary>
    private int GetNametableOffset(ushort address)
    {
        int local = (address - 0x2000) & 0x0FFF;   // $3000-$3EFF 是 $2000-$2EFF 的镜像
        int table = local / NametableSize;
        int inner = local % NametableSize;

        int bank = EffectiveMirroring switch
        {
            Mirroring.Vertical => table & 0x01,
            Mirroring.Horizontal => table >> 1,
            Mirroring.SingleScreenLower => 0,
            Mirroring.SingleScreenUpper => 1,
            _ => table,                          // FourScreen：4 块独立
        };

        return (bank * NametableSize) + inner;
    }

    /// <summary>$3F00-$3FFF：低 5 位寻址，$3F10/$3F14/$3F18/$3F1C 镜像回 $3F00/$3F04/$3F08/$3F0C。</summary>
    private static int GetPaletteOffset(ushort address)
    {
        int index = address & 0x1F;
        if (index >= 0x10 && (index & 0x03) == 0)
        {
            index -= 0x10;
        }

        return index;
    }
}
