namespace GodotNes.Core;

/// <summary>
/// CPU 地址空间（$0000-$FFFF）。CPU 只通过它读写内存，
/// 这样以后要在读写里插入周期计数、DMA 停顿等时序逻辑时不用动 CPU。
/// </summary>
public interface IBus
{
	byte Read(ushort address);

	void Write(ushort address, byte value);

	/// <summary>
	/// 写 $4014（OAM DMA）之后置位：CPU 把这次 DMA 的停顿周期算进去。
	/// 真机写完 $4014 会停 513 个周期（写在奇数周期上时是 514），这段时间 CPU 不执行指令。
	///
	/// 别的总线实现（比如只测 CPU 的假总线）可以一直返回 false，不实现也没关系 ——
	/// 但 `--trace-cpu` 的差分测试要和参照实现对齐时序时就得照做。
	/// </summary>
	bool OamDmaStallPending { get; set; }
}
