// 把 A 画面相对 B 画面按行上下平移，找最佳匹配偏移 —— 用来判断"背景整体上移/下移"这类卷轴问题。
// 两个文件都是 256x240 的调色板索引帧（每字节 0-63）。
//
//   node .ref/frame-shift.js <a.idx> <b.idx>

import fs from "fs";

const W = 256;
const H = 240;

const [, , pathA, pathB] = process.argv;
const a = fs.readFileSync(pathA);
const b = fs.readFileSync(pathB);

function diffAtShift(shift) {
	let diff = 0;
	for (let y = 0; y < H; y++) {
		const sy = y + shift;
		if (sy < 0 || sy >= H) {
			diff += W;
			continue;
		}
		for (let x = 0; x < W; x++) {
			if (a[y * W + x] !== b[sy * W + x]) diff++;
		}
	}
	return diff;
}

console.log("偏移(行)   差异像素   说明");
let best = { shift: 0, diff: Infinity };
for (let shift = -80; shift <= 80; shift++) {
	const diff = diffAtShift(shift);
	if (diff < best.diff) best = { shift, diff };
}
for (const shift of [0, best.shift]) {
	console.log(`  ${String(shift).padStart(4)}   ${String(diffAtShift(shift)).padStart(8)}`);
}
console.log(`最佳：A 相对 B 平移 ${best.shift} 行时差异 ${best.diff} 像素（原始 0 行差异 ${diffAtShift(0)}）`);

// 再按"上/下两半分开"看：HUD 区（0-31）、场景区（32-191）、底部（192-239）
function diffRegion(y0, y1, shift) {
	let diff = 0;
	for (let y = y0; y < y1; y++) {
		const sy = y + shift;
		if (sy < 0 || sy >= H) { diff += W; continue; }
		for (let x = 0; x < W; x++) if (a[y * W + x] !== b[sy * W + x]) diff++;
	}
	return diff;
}
console.log("分区域（最佳偏移下）：");
for (const [name, y0, y1] of [["顶部 HUD", 0, 32], ["场景", 32, 192], ["底部 HUD", 192, 240]]) {
	console.log(`  ${name.padEnd(8)} 原始 ${String(diffRegion(y0, y1, 0)).padStart(6)}  最佳偏移(${best.shift}) ${String(diffRegion(y0, y1, best.shift)).padStart(6)}`);
}
