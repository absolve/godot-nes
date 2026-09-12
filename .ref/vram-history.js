// 追踪指定 VRAM 地址区间的写入历史（从 --log-ppu 还原 $2006/$2007 序列）。
//
//   node .ref/vram-history.js <ppu-log> <起始地址> <结束地址>

import fs from "fs";

const [, , logPath, fromStr, toStr] = process.argv;
const lo = parseInt(fromStr, 16);
const hi = parseInt(toStr, 16);

const lines = fs.readFileSync(logPath, "utf8").split(/\r?\n/);

let addrHi = null;
let addr = 0;
let addrValid = false;
let count = 0;
const shown = [];

for (const line of lines) {
	if (!line) continue;
	const p = line.split(" ");
	if (p.length < 4) continue;
	const frame = parseInt(p[0], 10);
	const scanline = parseInt(p[1], 10);
	const reg = p[2];
	const value = parseInt(p[3], 16);
	if (Number.isNaN(frame)) continue;

	if (reg === "0006") {
		if (addrHi === null) {
			addrHi = value;
		} else {
			addr = ((addrHi & 0x3f) << 8) | value;
			addrHi = null;
			addrValid = true;
		}
	} else if (reg === "0007" && addrValid) {
		const a = addr & 0x3fff;
		if (a >= lo && a <= hi) {
			count++;
			// 同一批连续写只打一次头，避免刷屏
			if (shown.length < 60) shown.push({ frame, scanline, a, value });
		}
		addr = (addr + 1) & 0x7fff;
	}
}

console.log(`地址 $${lo.toString(16).toUpperCase()}-$${hi.toString(16).toUpperCase()} 一共被写 ${count} 次`);
console.log(`（只列前 60 次）`);
for (const w of shown) {
	console.log(`   ${w.frame} 扫描线${String(w.scanline).padStart(3)} $${w.a.toString(16).toUpperCase().padStart(4, "0")} ← $${w.value.toString(16).toUpperCase().padStart(2, "0")}`);
}
