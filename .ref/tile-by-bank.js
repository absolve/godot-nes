// 把每个 CHR bank 的同一个图块号排成一张网格图（32 列 × 4 行 = 128 个 bank）。
// 用来定位"图块号 X 在哪个 bank 里画出来是某个字形"，从而反查
// MMC3 的 bank 号 → CHR 偏移 换算到底哪里不对。
//
//   node .ref/tile-by-bank.js <rom.nes> <图块号(hex)> <out.idx>

import fs from "fs";

const [, , romPath, tileStr, outPath] = process.argv;
const tile = parseInt(tileStr, 16);

const rom = fs.readFileSync(romPath);
const prgSize = rom[4] * 16384;
const chrSize = rom[5] * 8192;
const chrStart = 16 + prgSize;
const chr = rom.subarray(chrStart, chrStart + chrSize);
const banks = Math.floor(chrSize / 1024);

const W = 256;
const H = 240;
const out = Buffer.alloc(W * H, 0x0f);

for (let bank = 0; bank < Math.min(banks, 128); bank++) {
	const gx = (bank % 32) * 8;
	const gy = Math.floor(bank / 32) * 9;
	const tileOffset = bank * 1024 + tile * 16;
	for (let py = 0; py < 8; py++) {
		const low = chr[tileOffset + py] ?? 0;
		const high = chr[tileOffset + py + 8] ?? 0;
		for (let px = 0; px < 8; px++) {
			const bit = 7 - px;
			const colorBit = ((low >> bit) & 1) | (((high >> bit) & 1) << 1);
			if (colorBit === 0) continue;
			out[(gy + py) * W + gx + px] = 0x30;
		}
	}
}

fs.writeFileSync(outPath, out);
console.log(`已写出 ${outPath}：图块 $${tileStr.toUpperCase()} 在 bank $00-$7F 里的样子（每行 32 个，从 bank $00 开始依次排列）`);
