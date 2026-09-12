// 用 .nt（名称表+属性表）、.pal（调色板）和 ROM 里的 CHR，把名称表第 0-44 行分别用
// 两个 2KB BG bank 渲染出来，上下叠成一张 256x240 的索引图，交给 idx2png 转 PNG。
//
//   node .ref/render-bank.js <rom.nes> <mark-N.nt> <mark-N.pal> <bankA> <bankB> <out.idx>
//
// bankA 画在屏幕 0-44 行，bankB 画在 48-92 行。用来判断"顶部那段和底栏那段，
// 到底各自该用哪个 bank 的图案"。

import fs from "fs";

const [, , romPath, ntPath, palPath, bankAStr, bankBStr, outPath] = process.argv;
const W = 256;
const H = 240;

const rom = fs.readFileSync(romPath);
const prgSize = rom[4] * 16384;         // iNES 头：PRG 16KB 块数
const chrSize = rom[5] * 8192;
const chrStart = 16 + prgSize;
const chr = rom.subarray(chrStart, chrStart + chrSize);

const nt = fs.readFileSync(ntPath);
const pal = fs.readFileSync(palPath);

const out = Buffer.alloc(W * H, 0x0f);

function attributeFor(row, col) {
	// 属性表在名称表偏移 0x3C0，每 2x2 图块共用一个字节的 2 位
	const atIndex = 0x3C0 + ((row >> 2) * 8) + (col >> 2);
	const shift = ((row & 2) << 1) | (col & 2);
	return (nt[atIndex] >> shift) & 0x03;
}

function drawBank(bank, topRow) {
	const base = (bank & 0xff) * 1024;
	for (let row = 0; row < 45; row++) {
		for (let col = 0; col < 32; col++) {
			const tile = nt[row * 32 + col];
			const paletteIndex = attributeFor(row, col);
			const tileOffset = base + tile * 16;
			for (let py = 0; py < 8; py++) {
				const low = chr[tileOffset + py] ?? 0;
				const high = chr[tileOffset + py + 8] ?? 0;
				for (let px = 0; px < 8; px++) {
					const bit = 7 - px;
					const colorBit = ((low >> bit) & 1) | (((high >> bit) & 1) << 1);
					if (colorBit === 0) continue;   // 0 号色是背景色，留空
					const paletteValue = pal[(paletteIndex << 2) + colorBit] & 0x3f;
					const x = col * 8 + px;
					const y = topRow + row * 8 + py;
					if (x < W && y < H) out[y * W + x] = paletteValue;
				}
			}
		}
	}
}

drawBank(parseInt(bankAStr, 16), 0);
drawBank(parseInt(bankBStr, 16), 48);

fs.writeFileSync(outPath, out);
console.log(`已写出 ${outPath}：上=bank $${bankAStr.toUpperCase()}，下=bank $${bankBStr.toUpperCase()}`);
