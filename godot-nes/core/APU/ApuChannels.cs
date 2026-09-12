namespace GodotNes.Core;

/// <summary>长度计数器查表：索引是写进 $4003/$4007/$400B 的高 5 位，值是长度（单位"半帧"）。</summary>
internal static class LengthTable
{
    public static readonly byte[] Values =
    {
        10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12,
        26, 14, 12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26,
        16, 28, 32, 30,
    };
}

/// <summary>
/// 包络发生器，脉冲和噪声共用一套。
/// 在"四分帧"上时钟：从 15 往 0 衰减；常量音量模式下直接输出音量值。
/// </summary>
public sealed class Envelope
{
    private bool _restart;
    private int _divider;

    /// <summary>循环标志：衰减到 0 后回到 15。</summary>
    public bool Loop { get; set; }

    /// <summary>常量音量模式（为 1 时忽略包络，直接用 Volume）。</summary>
    public bool Constant { get; set; }

    /// <summary>音量 / 包络分频。</summary>
    public int Volume { get; set; }

    /// <summary>当前衰减值。</summary>
    public int Decay { get; private set; }

    /// <summary>写 length 寄存器时调用，下一拍重载。</summary>
    public void Restart() => _restart = true;

    public int Output => Constant ? Volume : Decay;

    public void Clock()
    {
        if (_restart)
        {
            _restart = false;
            Decay = 15;
            _divider = Volume;
            return;
        }

        if (_divider > 0)
        {
            _divider--;
            return;
        }

        _divider = Volume;
        if (Decay > 0)
        {
            Decay--;
        }
        else if (Loop)
        {
            Decay = 15;
        }
    }
}

/// <summary>脉冲通道。4 种占空比 + 长度计数器 + 包络 + 扫频。</summary>
public sealed class PulseChannel
{
    private static readonly byte[][] DutyTable =
    {
        new byte[] { 0, 1, 0, 0, 0, 0, 0, 0 },
        new byte[] { 0, 1, 1, 0, 0, 0, 0, 0 },
        new byte[] { 0, 1, 1, 1, 1, 0, 0, 0 },
        new byte[] { 1, 0, 0, 1, 1, 1, 1, 1 },
    };

    private int _timer;
    private int _sequence;
    private bool _sweepEnabled;
    private bool _sweepNegate;
    private bool _sweepReload;
    private int _sweepPeriod;
    private int _sweepShift;
    private int _sweepDivider;

    public Envelope Envelope { get; } = new();

    public bool Enabled { get; set; }

    public int LengthCounter { get; set; }

    /// <summary>长度计数器暂停（$4000 bit5）。</summary>
    public bool Halt { get; set; }

    public int TimerPeriod { get; set; }

    /// <summary>占空比（$4000 bit6-7）。</summary>
    public int Duty { get; set; }

    /// <summary>
    /// 脉冲 2 的扫频取反是"减去变化量"，脉冲 1 是"减去变化量 + 1"。
    /// 真机上这个差能听出来，所以两个实例不能共用一套公式。
    /// </summary>
    public bool IsSecondPulse { get; set; }

    public void WriteSweep(byte value)
    {
        _sweepEnabled = (value & 0x80) != 0;
        _sweepPeriod = (value >> 4) & 0x07;
        _sweepNegate = (value & 0x08) != 0;
        _sweepShift = value & 0x07;
        _sweepReload = true;
    }

    /// <summary>APU 周期（2 个 CPU 周期）调用一次。</summary>
    public void ClockTimer()
    {
        if (_timer > 0)
        {
            _timer--;
            return;
        }

        _timer = TimerPeriod;
        _sequence = (_sequence + 1) & 0x07;
    }

    /// <summary>半帧：长度计数器。</summary>
    public void ClockLength()
    {
        if (!Halt && LengthCounter > 0)
        {
            LengthCounter--;
        }
    }

    /// <summary>半帧：扫频。</summary>
    public void ClockSweep()
    {
        if (_sweepDivider == 0 && _sweepEnabled && _sweepShift > 0 && !IsMuted())
        {
            int change = TimerPeriod >> _sweepShift;
            TimerPeriod += _sweepNegate ? -(IsSecondPulse ? change : change + 1) : change;
            if (TimerPeriod < 0)
            {
                TimerPeriod = 0;
            }
        }

        if (_sweepDivider == 0 || _sweepReload)
        {
            _sweepDivider = _sweepPeriod;
            _sweepReload = false;
        }
        else
        {
            _sweepDivider--;
        }
    }

    /// <summary>周期小于 8、或者扫频后的目标周期超过 $7FF，都静音（真机行为）。</summary>
    private bool IsMuted()
    {
        if (TimerPeriod < 8)
        {
            return true;
        }

        int change = TimerPeriod >> _sweepShift;
        int target = TimerPeriod + (_sweepNegate ? -(IsSecondPulse ? change : change + 1) : change);
        return target > 0x7FF;
    }

    public int Output()
    {
        if (!Enabled || LengthCounter == 0 || IsMuted())
        {
            return 0;
        }

        return DutyTable[Duty][_sequence] != 0 ? Envelope.Output : 0;
    }
}

/// <summary>三角波通道。32 级阶梯，带长度计数器和线性计数器。</summary>
public sealed class TriangleChannel
{
    private static readonly byte[] Sequence =
    {
        15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    };

    private int _timer;
    private int _sequence;
    private int _level;

    public bool Enabled { get; set; }

    public int LengthCounter { get; set; }

    /// <summary>同时控制长度计数器和线性计数器是否暂停（$4008 bit7）。</summary>
    public bool Halt { get; set; }

    /// <summary>线性计数器重载值（$4008 低 7 位）。</summary>
    public int LinearReload { get; set; }

    public int LinearCounter { get; set; }

    public int TimerPeriod { get; set; }

    /// <summary>
    /// 每个 CPU 周期调用一次（三角波比其他通道快一倍）。
    ///
    /// 关键：长度计数器或线性计数器为 0 时**定序器停住、输出电平保持**（真机行为）。
    /// 之前的实现不管三七二十一一直往前走阶梯、计数器一清零就直接输出 0，
    /// 结果每个音符结束时电平是"跳"到 0 的 —— 低音声部会一下一下地"咔"。
    /// </summary>
    public void ClockTimer()
    {
        if (!Enabled || LengthCounter == 0 || LinearCounter == 0)
        {
            return;
        }

        if (_timer > 0)
        {
            _timer--;
            return;
        }

        _timer = TimerPeriod;
        _sequence = (_sequence + 1) & 0x1F;
        _level = Sequence[_sequence];
    }

    public void ClockLength()
    {
        if (!Halt && LengthCounter > 0)
        {
            LengthCounter--;
        }
    }

    /// <summary>四分帧：线性计数器。</summary>
    public void ClockLinear()
    {
        if (LinearCounter > 0)
        {
            LinearCounter--;
        }

        if (LinearReload > 0)
        {
            LinearCounter = LinearReload;
        }

        if (!Halt)
        {
            LinearReload = 0;
        }
    }

    /// <summary>当前电平。不活动时保持上一次的值（真机 DAC 是"保持"而不是归零）。</summary>
    public int Output() => _level;
}

/// <summary>噪声通道。15 位 LFSR，两种抽头模式。</summary>
public sealed class NoiseChannel
{
    private static readonly int[] PeriodTable =
    {
        4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068,
    };

    private int _timer;
    private int _shift = 1;   // 不能是全 0，否则 LFSR 卡死

    public Envelope Envelope { get; } = new();

    public bool Enabled { get; set; }

    public int LengthCounter { get; set; }

    public bool Halt { get; set; }

    /// <summary>短模式（$400E bit7）：抽头换成 bit0^bit6，音色更"金属"。</summary>
    public bool ShortMode { get; set; }

    /// <summary>周期索引（$400E 低 4 位）。</summary>
    public int PeriodIndex { get; set; }

    /// <summary>每个 CPU 周期调用一次（周期表本身就是 CPU 周期数）。</summary>
    public void ClockTimer()
    {
        if (--_timer > 0)
        {
            return;
        }

        _timer += PeriodTable[PeriodIndex];
        int feedback = ShortMode ? (_shift ^ (_shift >> 6)) & 1 : (_shift ^ (_shift >> 1)) & 1;
        _shift = (_shift >> 1) | (feedback << 14);
    }

    public void ClockLength()
    {
        if (!Halt && LengthCounter > 0)
        {
            LengthCounter--;
        }
    }

    /// <summary>LFSR 的最低位为 1 时静音。</summary>
    public int Output() =>
        !Enabled || LengthCounter == 0 || (_shift & 1) != 0 ? 0 : Envelope.Output;
}

/// <summary>
/// DMC（差分调制）通道：播放一段采样，输出 7 位电平。
/// 简化点：没有实现它对 CPU 的周期偷取（那属于总线时序，见实现文档的待办）。
/// </summary>
public sealed class DmcChannel
{
    private static readonly int[] PeriodTable =
    {
        428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54,
    };

    private readonly Apu _apu;
    private int _timer;
    private int _bytesRemaining;
    private int _currentAddress;
    private int _shiftRegister;
    private int _bitsRemaining;

    public DmcChannel(Apu apu) => _apu = apu;

    public bool Enabled { get; set; }

    public bool IrqEnabled { get; set; }

    public bool Loop { get; set; }

    /// <summary>播放速率索引（$4010 低 4 位）。</summary>
    public int RateIndex { get; set; }

    /// <summary>7 位输出电平（DAC）。</summary>
    public int OutputLevel { get; set; }

    /// <summary>$4012 写入的采样起始地址（实际值是 $C000 + value*64）。</summary>
    public int SampleAddress { get; set; } = 0xC000;

    /// <summary>$4013 写入的采样长度（实际值是 value*16 + 1）。</summary>
    public int SampleLength { get; set; }

    public bool IrqPending { get; set; }

    /// <summary>还剩多少字节没读完（$4015 bit4 读的就是它）。</summary>
    public int LengthCounter { get; set; }

    /// <summary>重新开始播放（$4015 使能时调用）。</summary>
    public void Start()
    {
        _currentAddress = SampleAddress;
        _bytesRemaining = SampleLength;
        LengthCounter = SampleLength;
        _bitsRemaining = 0;
    }

    /// <summary>
    /// 每个 CPU 周期调用一次。
    ///
    /// **通道没使能时整个定时器都不走** —— 移位器停在原地，DAC 保持当前电平。
    /// 少了这个判断会出一个很典型的毛病：$4015 关掉 DMC 之后，残留的移位器会拿着
    /// 最后一个字节反复"按位加 2 / 减 2"，输出就以 DMC 速率一直抖 ——
    /// 听感是**持续的背景"嘶嘶"声**。fogleman / jsnes 都在这里直接 return，
    /// FCEUX 是把 DMCSize 清 0 让 DMA 与移位器停下来。
    ///
    /// 另外：移位器走空之后，只有**确实还有字节可读**才重新装载 8 位；
    /// 样本放完（且不循环）就停在那里，不能自己重新武装。
    /// </summary>
    public void ClockTimer()
    {
        if (!Enabled)
        {
            return;
        }

        if (_timer > 0)
        {
            _timer--;
            return;
        }

        _timer = PeriodTable[RateIndex];

        if (_bitsRemaining > 0)
        {
            ShiftBit();
            _bitsRemaining--;
            return;
        }

        if (_bytesRemaining == 0)
        {
            if (Loop)
            {
                Start();
            }
            else
            {
                if (IrqEnabled)
                {
                    IrqPending = true;
                }

                return;      // 没有数据了：DAC 就保持在这个电平上
            }
        }

        _shiftRegister = _apu.ReadMemory((ushort)_currentAddress);
        _currentAddress = _currentAddress == 0xFFFF ? 0x8000 : _currentAddress + 1;
        _bytesRemaining--;
        LengthCounter = _bytesRemaining;
        _bitsRemaining = 8;

        ShiftBit();
        _bitsRemaining--;
    }

    /// <summary>一位一位地调整输出电平：1 加 2，0 减 2（范围 0-127）。</summary>
    private void ShiftBit()
    {
        if ((_shiftRegister & 1) != 0)
        {
            if (OutputLevel <= 125)
            {
                OutputLevel += 2;
            }
        }
        else if (OutputLevel >= 2)
        {
            OutputLevel -= 2;
        }

        _shiftRegister >>= 1;
    }

    public int Output() => OutputLevel;
}
