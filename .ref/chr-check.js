// 字节级核对：把 emulator 快照里的 .chr（"当前映射出来的 8KB"）
// 与 ROM 文件里若干 1KB bank 直接比对，确认"我读的 CHR 内容"到底对应文件里的哪一段。
//
//   node .ref/chr-check.js <rom.nes> <mark.chr>

import fs from "fs";

const [, , romPath, chrPath] = process.argv;
const rom = fs.readFileSync(romPath);
const prgSize = rom[4] * 16384;
const chrStart = 16 + prgSize;
const chrSize = rom[5] * 8192;
const chr = rom.subarray(chrStart, chrStart + chrSize);
const snap = fs.readFileSync(chrPath);

console.log(`ROM CHR 基址 = ${chrStart}，大小 = ${chrSize}`);
console.log(`快照 .chr 大小 = ${snap.length}\n`);

// 快照的 8KB 对应 4 个连续 1KB bank，试着在 ROM 里找出它落在哪
let bestBank = -1;
for (let bank = 0; bank + 8 <= chrSize / 1024; bank++) {
	let same = true;
	for (let i = 0; i < 8192; i++) {
		if (chr[bank * 1024 + i] !== snap[i]) { same = false; break; }
	}
	if (same) { bestBank = bank; break; }
}
console.log(bestBank >= 0 ? `快照 8KB == ROM 的第 $${bestBank.toString(16)} 个 1KB bank 起 8KB` : "快照 8KB 在 ROM 里找不到连续匹配（说明映射是分段的，下面按每 1KB 单独找）");

console.log("\n按 1KB 分段查找（快照的第 i 个 1KB 对应 ROM 的哪个 bank）：");
for (let i = 0; i < 8; i++) {
	const seg = snap.subarray(i * 1024, (i + 1) * 1024);
	let found = -1;
	for (let bank = 0; bank < chrSize / 1024; bank++) {
		let same = true;
		for (let k = 0; k < 1024; k++) {
			if (chr[bank * 1024 + k] !== seg[k]) { same = false; break; }
		}
		if (same) { found = bank; break; }
	}
	console.log(`   快照 $${(i * 0x400).toString(16).toUpperCase().padStart(4, "0")}-$${((i + 1) * 0x400 - 1).toString(16).toUpperCase()} → ROM bank ${found >= 0 ? "$" + found.toString(16).toUpperCase() + " (" + found + ")" : "未找到"}`);
}

// 顺便：$40 和 $76 这两个"字体 bank"是不是同一份字体
function same1k(a, b) {
	for (let i = 0; i < 1024; i++) if (chr[a * 1024 + i] !== chr[b * 1024 + i]) return false;
	return true;
}
console.log("\nbanks 比较：");
console.log(`   $40 与 $76 相同? ${same1k(0x40, 0x76)}`);
console.log(`   $78 与 $76 相同? ${same1k(0x78, 0x76)}`);
console.log(`   $40 与 $78 相同? ${same1k(0x40, 0x78)}`);
