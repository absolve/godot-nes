using System.Collections.Generic;

namespace GodotNes.Core;

/// <summary>
/// mapper 号到家族名的对照表，只用于显示（"游戏相关信息"里写清楚这块卡带是什么芯片）。
///
/// 名字取自 Nesdev Wiki 的 mapper 列表和 FCEUX 的 bmap 表。注意：**这里列出名字不代表
/// 已经实现**，能不能跑要看 <see cref="MapperFactory.IsSupported"/>。
/// </summary>
public static class MapperNames
{
	private static readonly Dictionary<int, string> Names = new()
	{
		[0] = "NROM",
		[1] = "MMC1 (SxROM)",
		[2] = "UxROM",
		[3] = "CNROM",
		[4] = "MMC3 (TxROM)",
		[5] = "MMC5 (ExROM)",
		[7] = "AxROM",
		[9] = "MMC2 (PxROM)",
		[10] = "MMC4 (FxROM)",
		[11] = "Color Dreams",
		[13] = "CPROM",
		[15] = "100-in-1",
		[16] = "Bandai FCG",
		[18] = "Jaleco SS8806",
		[19] = "Namco 163",
		[21] = "Konami VRC4a/2a",
		[22] = "Konami VRC2a",
		[23] = "Konami VRC2b/4e",
		[24] = "Konami VRC6a",
		[25] = "Konami VRC4b",
		[26] = "Konami VRC6b",
		[32] = "Irem G-101",
		[33] = "Taito TC0190",
		[34] = "BNROM / NINA-001",
		[37] = "SMB + Duck Hunt (MMC1)",
		[41] = "Caltron 6-in-1",
		[42] = "FDS 转卡带 (FDSM)",
		[47] = "MMC3 克隆 (Super Spike V'Ball)",
		[64] = "Tengen RAMBO-1",
		[66] = "GxROM / MMC3 变体",
		[67] = "Sunsoft-3",
		[69] = "Sunsoft FME-7",
		[71] = "Camerica (Codemasters)",
		[73] = "Konami VRC3",
		[75] = "Konami VRC1",
		[78] = "Irem 74HC161/32",
		[79] = "NINA-03/06 (AVE)",
		[85] = "Konami VRC7",
		[87] = "Jaleco CHR 切换",
		[88] = "Namco 118 / MMC3 变体",
		[95] = "Namco 3425",
		[113] = "NINA-03/06 扩展",
		[118] = "TxSROM (MMC3)",
		[119] = "TQROM (MMC3)",
		[140] = "Jaleco JF-11/14",
		[150] = "Sachen 74*374",
		[159] = "Bandai FCG-1/2",
		[180] = "UNROM 变体",
		[185] = "CNROM 变体",
		[206] = "DxROM / Namco 118",
		[210] = "Namco 175/340",
		[228] = "Action 53 / Cheetahmen II",
		[232] = "Camerica Quattro",
		[240] = "C&E 字母选择",
		[246] = "C&E 时序",
		[255] = "110-in-1",
	};

	/// <summary>取 mapper 家族名，不认识就返回 "未知"。</summary>
	public static string GetName(int mapperNumber) =>
		Names.TryGetValue(mapperNumber, out string? name) ? name : "未知";
}
