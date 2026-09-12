using System;

namespace GodotNes.Core;

/// <summary>
/// 2A03 APU（5 个通道：脉冲 ×2、三角波、噪声、DMC）。
///
/// 设计思路：
///   - 按 **CPU 周期**推进（和 PPU 一样由 <see cref="NesConsole"/> 同步驱动）：
///     每个周期做一次帧计数器、三角波和 DMC；脉冲和噪声的定时器跑在 APU 周期上（2 个 CPU 周期一次）；
///   - 采样用**整数累加器**：每来一个 CPU 周期加一次采样率，超过 CPU 主频就吐一个样本。
///     不用浮点累加，跑久了也不会漂；
///   - 混音用 NES 那套非线性公式（脉冲一路、三角+噪声+DMC 一路），
///     全静音时两路正好都是 0，不会带直流偏移；
///   - 产出的样本先攒在缓冲区里，一帧结束由 <see cref="NesConsole"/> 一次性交给音频输出端。
///
/// 简化点：DMC 没有偷 CPU 周期（对声音没影响，只是时序上不如真机精确）。
/// </summary>
public sealed class Apu
{
    /// <summary>输出采样率。</summary>
    public const int SampleRate = 44100;

    /// <summary>NTSC CPU 主频，用来把 CPU 周期换算成采样点。</summary>
    public const int CpuFrequency = 1789773;

    private const int BufferCapacity = 4096;

    /// <summary>
    /// 直流消除器（一阶高通）的反馈系数 1/1024 —— 和 jsnes 的做法一致：
    /// 截止频率大约 7 Hz，只砍直流和极低频，音乐本身不受影响。
    /// </summary>
    private const float HighPassFeedback = 1f / 1024f;

    private readonly float[] _sampleBuffer = new float[BufferCapacity];
    private int _sampleCount;
    private int _sampleCounter;

    // 一个采样周期内各通道输出的累加值 + 周期数（采样时取平均，见 AddSample）。
    // 为什么要在**每个 CPU 周期**累加，而不是只在出样本的那一刻取瞬时值：
    // 44.1kHz 直接点采样方波会严重混叠 —— 高频方波会折回成一大堆和乐音无关的频率，
    // 听感就是"发毛、有噪音"。取一个采样周期内的平均等效于一次盒式低通，
    // 这也是 jsnes / fogleman 都用同一套做法的原因。
    private int _accumPulse;
    private int _accumTriangle;
    private int _accumNoise;
    private int _accumDmc;
    private int _accumCycles;

    // 直流消除器的状态
    private float _highPassPrevious;
    private float _highPassAccumulator;
    private bool _highPassReady;

    private int _cpuCycle;
    private int _frameCycle;

    public Apu()
    {
        Pulse1 = new PulseChannel();
        Pulse2 = new PulseChannel { IsSecondPulse = true };
        Triangle = new TriangleChannel();
        Noise = new NoiseChannel();
        Dmc = new DmcChannel(this);
    }

    public PulseChannel Pulse1 { get; private set; }

    public PulseChannel Pulse2 { get; private set; }

    public TriangleChannel Triangle { get; private set; }

    public NoiseChannel Noise { get; private set; }

    public DmcChannel Dmc { get; private set; }

    /// <summary>
    /// 复位。通道对象直接重建 —— 复位是低频操作，重建比给每个通道写一遍清空代码更省事也更不容易漏。
    /// </summary>
    public void Reset()
    {
        Pulse1 = new PulseChannel();
        Pulse2 = new PulseChannel { IsSecondPulse = true };
        Triangle = new TriangleChannel();
        Noise = new NoiseChannel();
        Dmc = new DmcChannel(this);

        _sampleCount = 0;
        _sampleCounter = 0;
        _accumPulse = 0;
        _accumTriangle = 0;
        _accumNoise = 0;
        _accumDmc = 0;
        _accumCycles = 0;
        _highPassPrevious = 0f;
        _highPassAccumulator = 0f;
        _highPassReady = false;
        _cpuCycle = 0;
        _frameCycle = 0;
        FrameIrqPending = false;
        FiveStepMode = false;
        IrqInhibit = false;
    }

    /// <summary>DMC 要读采样数据，装载卡带后由 <see cref="NesConsole"/> 注入。</summary>
    public Bus? Bus { get; set; }

    /// <summary>帧计数器 IRQ 是否挂起（$4017 bit6 = 0 时才会产生）。</summary>
    public bool FrameIrqPending { get; private set; }

    /// <summary>一共产生过多少次帧计数器 IRQ（排查"音乐不推进"最有用：正常应该 ≈ 帧数）。</summary>
    public long FrameIrqCount { get; private set; }

    /// <summary>一共写过多少次 $4017（写它会把帧计数器清零，写得太频繁就永远走不到步进点）。</summary>
    public long FrameCounterWrites { get; private set; }

    /// <summary>
    /// $4015 被访问时的回调，参数是 'R'（读状态）或 'W'（写使能）。调试用，默认 null。
    /// 游戏的音效驱动就是靠读 $4015 判断"声道播完了没"，排查"驱动卡在等声道"时最关键的一路。
    /// </summary>
    public Action<char>? StatusAccessHook { get; set; }

    private bool FiveStepMode { get; set; }

    private bool IrqInhibit { get; set; }

    public byte ReadMemory(ushort address) => Bus?.Read(address) ?? 0x00;

    // ------------------------------------------------------------------ 寄存器

    /// <summary>读 $4015：各通道长度计数器是否还在走，外加两个 IRQ 标志（读后清帧 IRQ）。</summary>
    public byte ReadStatus()
    {
        byte result = 0;

        if (Pulse1.LengthCounter > 0) { result |= 0x01; }
        if (Pulse2.LengthCounter > 0) { result |= 0x02; }
        if (Triangle.LengthCounter > 0) { result |= 0x04; }
        if (Noise.LengthCounter > 0) { result |= 0x08; }
        if (Dmc.LengthCounter > 0) { result |= 0x10; }
        if (FrameIrqPending) { result |= 0x40; }
        if (Dmc.IrqPending) { result |= 0x80; }

        FrameIrqPending = false;
        StatusAccessHook?.Invoke('R');
        return result;
    }

    /// <summary>写 $4015：通道使能。关掉某个通道会立刻清它的长度计数器。</summary>
    public void WriteStatus(byte value)
    {
        Pulse1.Enabled = (value & 0x01) != 0;
        Pulse2.Enabled = (value & 0x02) != 0;
        Triangle.Enabled = (value & 0x04) != 0;
        Noise.Enabled = (value & 0x08) != 0;

        if (!Pulse1.Enabled) { Pulse1.LengthCounter = 0; }
        if (!Pulse2.Enabled) { Pulse2.LengthCounter = 0; }
        if (!Triangle.Enabled) { Triangle.LengthCounter = 0; }
        if (!Noise.Enabled) { Noise.LengthCounter = 0; }

        bool dmcEnabled = (value & 0x10) != 0;
        if (!dmcEnabled)
        {
            Dmc.LengthCounter = 0;
        }
        else if (Dmc.LengthCounter == 0)
        {
            Dmc.Start();
        }

        Dmc.Enabled = dmcEnabled;
        Dmc.IrqPending = false;
        StatusAccessHook?.Invoke('W');
    }

    /// <summary>写 $4000-$4013（$4015/$4017 由总线分别转给 WriteStatus / WriteFrameCounter）。</summary>
    public void WriteRegister(ushort address, byte value)
    {
        switch (address)
        {
            // ---- 脉冲 1 ----
            case 0x4000: WritePulseControl(Pulse1, value); break;
            case 0x4001: Pulse1.WriteSweep(value); break;
            case 0x4002: Pulse1.TimerPeriod = (Pulse1.TimerPeriod & 0x700) | value; break;
            case 0x4003:
                Pulse1.TimerPeriod = (Pulse1.TimerPeriod & 0x0FF) | ((value & 0x07) << 8);
                if (Pulse1.Enabled)
                {
                    Pulse1.LengthCounter = LengthTable.Values[value >> 3];
                }

                Pulse1.Envelope.Restart();
                break;

            // ---- 脉冲 2 ----
            case 0x4004: WritePulseControl(Pulse2, value); break;
            case 0x4005: Pulse2.WriteSweep(value); break;
            case 0x4006: Pulse2.TimerPeriod = (Pulse2.TimerPeriod & 0x700) | value; break;
            case 0x4007:
                Pulse2.TimerPeriod = (Pulse2.TimerPeriod & 0x0FF) | ((value & 0x07) << 8);
                if (Pulse2.Enabled)
                {
                    Pulse2.LengthCounter = LengthTable.Values[value >> 3];
                }

                Pulse2.Envelope.Restart();
                break;

            // ---- 三角波 ----
            case 0x4008:
                Triangle.Halt = (value & 0x80) != 0;
                Triangle.LinearReload = value & 0x7F;
                break;
            case 0x400A: Triangle.TimerPeriod = (Triangle.TimerPeriod & 0x700) | value; break;
            case 0x400B:
                Triangle.TimerPeriod = (Triangle.TimerPeriod & 0x0FF) | ((value & 0x07) << 8);
                if (Triangle.Enabled)
                {
                    Triangle.LengthCounter = LengthTable.Values[value >> 3];
                }

                Triangle.LinearCounter = Triangle.LinearReload;
                break;

            // ---- 噪声 ----
            case 0x400C: WriteNoiseControl(value); break;
            case 0x400E:
                Noise.ShortMode = (value & 0x80) != 0;
                Noise.PeriodIndex = value & 0x0F;
                break;
            case 0x400F:
                if (Noise.Enabled)
                {
                    Noise.LengthCounter = LengthTable.Values[value >> 3];
                }

                Noise.Envelope.Restart();
                break;

            // ---- DMC ----
            case 0x4010:
                Dmc.IrqEnabled = (value & 0x80) != 0;
                Dmc.Loop = (value & 0x40) != 0;
                Dmc.RateIndex = value & 0x0F;
                if (!Dmc.IrqEnabled)
                {
                    Dmc.IrqPending = false;
                }

                break;
            case 0x4011: Dmc.OutputLevel = value & 0x7F; break;
            case 0x4012: Dmc.SampleAddress = 0xC000 + (value * 64); break;
            case 0x4013: Dmc.SampleLength = (value * 16) + 1; break;
        }
    }

    private static void WritePulseControl(PulseChannel pulse, byte value)
    {
        pulse.Duty = (value >> 6) & 0x03;
        pulse.Halt = (value & 0x20) != 0;
        pulse.Envelope.Constant = (value & 0x10) != 0;
        pulse.Envelope.Volume = value & 0x0F;
        pulse.Envelope.Loop = pulse.Halt;
    }

    private void WriteNoiseControl(byte value)
    {
        Noise.Halt = (value & 0x20) != 0;
        Noise.Envelope.Constant = (value & 0x10) != 0;
        Noise.Envelope.Volume = value & 0x0F;
        Noise.Envelope.Loop = Noise.Halt;
    }

    /// <summary>写 $4017：帧计数器模式，并复位序列。</summary>
    public void WriteFrameCounter(byte value)
    {
        FrameCounterWrites++;

        FiveStepMode = (value & 0x80) != 0;
        IrqInhibit = (value & 0x40) != 0;
        if (IrqInhibit)
        {
            FrameIrqPending = false;
        }

        _frameCycle = 0;

        // 5 步模式下写 $4017 会立刻走一次四分帧 + 半帧
        if (FiveStepMode)
        {
            ClockQuarterFrame();
            ClockHalfFrame();
        }
    }

    // ------------------------------------------------------------------ 时序

    /// <summary>推进 <paramref name="cpuCycles"/> 个 CPU 周期。</summary>
    public void Step(int cpuCycles)
    {
        for (int i = 0; i < cpuCycles; i++)
        {
            StepFrameCounter();

            Triangle.ClockTimer();
            Dmc.ClockTimer();

            // 噪声的周期表单位是 **CPU 周期**（4 / 8 / 16 … 4068），所以它每个 CPU 周期走一次。
            // 之前和脉冲一起放在"每 2 个 CPU 周期"那一组里，周期被拉成了两倍 —— 噪声整体低了一个八度。
            Noise.ClockTimer();

            // 脉冲的定时器跑在 APU 周期上：每 2 个 CPU 周期动一次
            if ((_cpuCycle & 1) == 0)
            {
                Pulse1.ClockTimer();
                Pulse2.ClockTimer();
            }

            _cpuCycle++;

            // 把这几周期各通道的输出累加进"本采样周期"的积分器。
            //
            // 只在 APU 周期（偶数 CPU 周期）上采样一次，把结果按 2 个周期计入 ——
            // 通道输出最快也就每个 APU 周期变一次（三角波约 100+ 个周期才走一级阶梯），
            // 隔一个周期取一次对盒式平均的结果没有可听出来的影响，但省掉了一半的调用。
            // 这个函数是整个模拟器最热的循环（一帧 29780 次），值得抠。
            if ((_cpuCycle & 1) == 0)
            {
                _accumPulse += (Pulse1.Output() + Pulse2.Output()) << 1;
                _accumTriangle += Triangle.Output() << 1;
                _accumNoise += Noise.Output() << 1;
                _accumDmc += Dmc.Output() << 1;
                _accumCycles += 2;
            }

            _sampleCounter += SampleRate;
            if (_sampleCounter >= CpuFrequency)
            {
                _sampleCounter -= CpuFrequency;
                AddSample();
            }
        }
    }

    private void StepFrameCounter()
    {
        _frameCycle++;

        if (FiveStepMode)
        {
            // 5 步模式：四分帧在 7457 / 14913 / 22371 / 29829 / 37281 各一次（5 次），
            // 半帧（长度 + 扫频）在 14913 / 29829 / 37281（3 次）。没有 IRQ。
            switch (_frameCycle)
            {
                case 7457:
                case 14913:
                case 22371:
                case 29829:
                case 37281:
                    ClockQuarterFrame();
                    break;
            }

            switch (_frameCycle)
            {
                case 14913:
                case 29829:
                case 37281:
                    ClockHalfFrame();
                    break;
            }

            if (_frameCycle >= 37282)
            {
                _frameCycle = 0;
            }

            return;
        }

        // 4 步模式：四分帧 7457 / 14913 / 22371 / 29829（4 次），
        // 半帧 14913 / 29829（2 次），第 4 步拉帧 IRQ。
        //
        // 注意 14913 那一次**两种时钟都要走** —— 一开始漏了 14913 的四分帧，
        // 包络和三角波线性计数器就只按 3/4 的速度走，音乐的音量和音符长度全是错的。
        switch (_frameCycle)
        {
            case 7457:
            case 14913:
            case 22371:
            case 29829:
                ClockQuarterFrame();
                break;
        }

        switch (_frameCycle)
        {
            case 14913:
            case 29829:
                ClockHalfFrame();
                break;
        }

        if (_frameCycle == 29829 && !IrqInhibit)
        {
            FrameIrqPending = true;
            FrameIrqCount++;
        }

        if (_frameCycle >= 29830)
        {
            _frameCycle = 0;
        }
    }

    /// <summary>四分帧：包络 + 三角波线性计数器。</summary>
    private void ClockQuarterFrame()
    {
        Pulse1.Envelope.Clock();
        Pulse2.Envelope.Clock();
        Noise.Envelope.Clock();
        Triangle.ClockLinear();
    }

    /// <summary>半帧：长度计数器 + 扫频。</summary>
    private void ClockHalfFrame()
    {
        Pulse1.ClockLength();
        Pulse1.ClockSweep();
        Pulse2.ClockLength();
        Pulse2.ClockSweep();
        Triangle.ClockLength();
        Noise.ClockLength();
    }

    // ------------------------------------------------------------------ 输出

    /// <summary>取走攒下的样本，返回实际写入的个数。</summary>
    public int DrainSamples(Span<float> destination)
    {
        int count = Math.Min(_sampleCount, destination.Length);
        _sampleBuffer.AsSpan(0, count).CopyTo(destination);

        // 没取完的挪到缓冲区最前面
        if (count < _sampleCount)
        {
            _sampleBuffer.AsSpan(count, _sampleCount - count).CopyTo(_sampleBuffer);
        }

        _sampleCount -= count;
        return count;
    }

    /// <summary>
    /// 混音：NES 用的是非线性混合 —— 脉冲一路，"三角 + 噪声 + DMC"另一路。
    /// 输入是**本采样周期内各通道输出的平均值**（盒式滤波，见 <see cref="Step"/>），
    /// 不是瞬时值；两路都按 NES 的非线性曲线换算，再减掉直流、过一次一阶高通。
    /// </summary>
    private void AddSample()
    {
        float cycles = _accumCycles == 0 ? 1f : _accumCycles;
        float pulse1 = _accumPulse / cycles;          // 0-30（两个 4 位通道之和）
        float triangle = _accumTriangle / cycles;      // 0-15
        float noise = _accumNoise / cycles;            // 0-15
        float dmc = _accumDmc / cycles;                // 0-127

        _accumPulse = 0;
        _accumTriangle = 0;
        _accumNoise = 0;
        _accumDmc = 0;
        _accumCycles = 0;

        if (_sampleCount >= BufferCapacity)
        {
            return;   // 缓冲满了就丢；正常情况下取数比产数快
        }

        float pulse = pulse1 > 0f ? 95.88f / ((8128f / pulse1) + 100f) : 0f;

        float tnd = 0f;
        if (triangle + noise + dmc > 0f)
        {
            float tndSum = (triangle / 8227f) + (noise / 12241f) + (dmc / 22638f);
            tnd = 159.79f / ((1f / tndSum) + 100f);
        }

        // 真机 DAC 的输出是单极性的（0 到满量程），照原样播出去等于带上一个恒定直流：
        // 既吃掉动态范围，又会在出第一个音的时候"咚"一下。这里用一阶高通把直流滤掉。
        // 第一个样本把滤波器状态对齐到当前值，免得开机先来一下跳变。
        float mixed = pulse + tnd;
        if (!_highPassReady)
        {
            _highPassReady = true;
            _highPassPrevious = mixed;
        }

        float difference = mixed - _highPassPrevious;
        _highPassPrevious += difference;
        _highPassAccumulator += difference - (_highPassAccumulator * HighPassFeedback);

        _sampleBuffer[_sampleCount++] = _highPassAccumulator;
    }
}
