using System;
using System.Collections.Generic;

namespace GodotNes.Core;

/// <summary>
/// 按 mapper 号创建映射器。新增 mapper 时只要在 <see cref="Factories"/> 里注册一行。
///
/// 已实现：0 (NROM)、1 (MMC1)、2 (UxROM)、3 (CNROM)、4 (MMC3)、7 (AxROM/AOROM)。
/// 这几个覆盖了 NES 上绝大多数商业卡带；再往后的 5/9/10/19/69 等按需再加。
/// </summary>
public static class MapperFactory
{
	private static readonly Dictionary<int, Func<Cartridge, Mapper>> Factories = new()
	{
		[0] = static cartridge => new NromMapper(cartridge),
		[1] = static cartridge => new Mmc1Mapper(cartridge),
		[2] = static cartridge => new UxromMapper(cartridge),
		[3] = static cartridge => new CnromMapper(cartridge),
		[4] = static cartridge => new Mmc3Mapper(cartridge),
		[7] = static cartridge => new AxromMapper(cartridge),
		[23] = static cartridge => new Vrc2Mapper(cartridge),
	};

	/// <summary>已实现的 mapper 号。</summary>
	public static IReadOnlyCollection<int> SupportedMappers => Factories.Keys;

	public static bool IsSupported(int mapperNumber) => Factories.ContainsKey(mapperNumber);

	public static Mapper Create(Cartridge cartridge)
	{
		if (cartridge is null)
		{
			throw new ArgumentNullException(nameof(cartridge));
		}

		if (Factories.TryGetValue(cartridge.MapperNumber, out Func<Cartridge, Mapper>? factory))
		{
			return factory(cartridge);
		}

		throw new NotSupportedException(
			$"暂不支持 Mapper {cartridge.MapperNumber}（已实现：{string.Join(", ", Factories.Keys)}）。" +
			"还可以考虑用支持该 mapper 的模拟器玩，或者按 godot-nes-emulator-plan.md 的清单继续补。");
	}
}
