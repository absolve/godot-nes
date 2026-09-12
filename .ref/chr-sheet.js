// 把指定 CHR bank 的图块以"图块表"形式画出来，一次看几个 bank 的字体是否正常。
// 用来判断 MMC3 的 CHR bank 号 → CHR 偏移 的换算对不对：
// 如果某个 bank 里 $01-$0A 是数字、$73 附近是字母，那就是"字体 bank"。
//
//   node .ref/chr-sheet.js <rom.nes> <bank1> <bank2> ... <out.idx>

import fs from "fs";

const args = process.argv.slice(2);
const outPath = args.pop();
const romPath = args.shift();
const banks = args.map((s) => parseInt(s, 16));

const rom = fs.readFileSync(romPath);
const prgSize = rom[4] * 16384;
const chrSize = rom[5] * 8192;
const chrStart = 16 + prgSize;
const chr = rom.subarray(chrStart, chrStart + chrSize);

const W = 256;
const H = 240;
const out = Buffer.alloc(W * H, 0x0f);   // 背景：调色板 $0F（黑）

const TILES_PER_ROW = 32;
const SHEET_H = 16;                       // 每个 bank 画高 16 行像素（32 个图块）

banks.forEach((bank, index) => {
	const base = (bank & 0xff) * 1024;
	const top = index * (SHEET_H + 2);
	for (let t = 0; t < 64; t++) {
		const gx = (t % TILES_PER_ROW) * 8;
		const gy = top + Math.floor(t / TILES_PER_ROW) * 8;
		const tileOffset = base + t * 16;
		for (let py = 0; py < 8; py++) {
			const low = chr[tileOffset + py] ?? 0;
			const high = chr[tileOffset + py + 8] ?? 0;
			for (let px = 0; px < 8; px++) {
				const bit = 7 - px;
				const colorBit = ((low >> bit) & 1) | (((high >> bit) & 1) << 1);
				if (colorBit === 0) continue;
				const x = gx + px;
				const y = gy + py;
				if (x < W && y < H) out[y * W + x] = 0x30;   // 白色
			}
		}
	}
});

fs.writeFileSync(outPath, out);
console.log(`已写出 ${outPath}：依次是 bank ${banks.map((b) => "$" + b.toString(16).toUpperCase()).join(", ")} 的图块 $00-$3F`);
