// 在 FCEUX 存档的解压数据里搜索"和我名称表某几行最相似"的 2KB 区域。
// 底栏 HUD 所在的行（名称表第 24-28 行）是静态内容，与关卡位置无关，
// 所以即使两次运行位置不同，也能用它判断两边的名称表是否一致。
//
//   node .ref/find-nametable.js <存档> <我的.nt>

import fs from "fs";
import zlib from "zlib";

const [, , statePath, myNtPath] = process.argv;
const raw = fs.readFileSync(statePath);
const comprLen = raw.readUInt32LE(12);
const body = raw.subarray(16);
const payload = comprLen === 0xffffffff ? body : zlib.inflateSync(body);
const mine = fs.readFileSync(myNtPath);

// 用我的第 24-28 行（160 字节）当模板，在 payload 里滑窗找最像的位置
const start = 24 * 32;
const len = 5 * 32;
const template = mine.subarray(start, start + len);

let best = { off: -1, same: -1 };
for (let i = 0; i + len <= payload.length; i++) {
	let same = 0;
	for (let k = 0; k < len; k++) if (payload[i + k] === template[k]) same++;
	if (same > best.same) best = { off: i, same };
}
console.log(`在我的名称表第 24-28 行（${len} 字节）里，最相似的 payload 位置：偏移 ${best.off}，相同 ${best.same}/${len}（${((best.same / len) * 100).toFixed(0)}%）`);

// 以最佳位置为基准，看看能否对齐出一个完整的名称表，并逐行对比
if (best.same > len * 0.5) {
	const ntStart = best.off - start;
	if (ntStart >= 0 && ntStart + 2048 <= payload.length) {
		const theirs = payload.subarray(ntStart, ntStart + 2048);
		console.log(`\n推测 FCEUX 的物理名称表在 payload 偏移 ${ntStart}，逐行对比：`);
		for (const row of [0, 1, 2, 3, 12, 13, 24, 25, 26, 27, 28, 29, 30, 31]) {
			const a = mine.subarray(row * 32, row * 32 + 32);
			const b = theirs.subarray(row * 32, row * 32 + 32);
			let same = 0;
			for (let k = 0; k < 32; k++) if (a[k] === b[k]) same++;
			console.log(`   行${String(row).padStart(2)}: 相同 ${same}/32 ${same === 32 ? "✅" : "❌"}`);
			if (same < 32) {
				console.log(`      我   : ${Array.from(a).map((v) => v.toString(16).toUpperCase().padStart(2, "0")).join(" ")}`);
				console.log(`      FCEUX: ${Array.from(b).map((v) => v.toString(16).toUpperCase().padStart(2, "0")).join(" ")}`);
			}
		}
	} else {
		console.log("（最佳位置靠近边界，无法还原完整名称表）");
	}
} else {
	console.log("\n相似度太低 → 两边名称表内容差别很大");
}
