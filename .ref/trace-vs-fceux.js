// 逐帧对比我的 RAM 轨迹和 FCEUX 的轨迹，只看"游戏真正写过"的地址。
// FCEUX 的 RAM 初值是 FF，所以"全程都是 FF"的地址就是游戏从没写过的，必须排除。
//
//   node .ref/trace-vs-fceux.js <mine.bin> <fceux.bin>

import fs from "fs";

const [, , minePath, fceuxPath] = process.argv;
const mine = fs.readFileSync(minePath);
const fceux = fs.readFileSync(fceuxPath);
const frameSize = 2048;
const frames = Math.min(Math.floor(mine.length / frameSize), Math.floor(fceux.length / frameSize));

console.log(`我 ${Math.floor(mine.length / frameSize)} 帧，FCEUX ${Math.floor(fceux.length / frameSize)} 帧 → 对比 ${frames} 帧`);

// 活地址：FCEUX 至少写过一次（值不再是 FF）
const live = [];
for (let i = 0; i < frameSize; i++) {
	let used = false;
	for (let f = 0; f < frames; f++) {
		if (fceux[f * frameSize + i] !== 0xff) { used = true; break; }
	}
	if (used) live.push(i);
}
console.log(`游戏真正使用的地址：${live.length} / 2048`);

function diffAt(f) {
	const base = f * frameSize;
	let d = 0;
	const bytes = [];
	for (const i of live) {
		if (mine[base + i] !== fceux[base + i]) {
			d++;
			if (bytes.length < 20) bytes.push([i, mine[base + i], fceux[base + i]]);
		}
	}
	return { d, bytes };
}

let firstDiff = -1;
for (let f = 0; f < frames; f++) {
	if (diffAt(f).d > 0) { firstDiff = f; break; }
}

if (firstDiff < 0) {
	console.log("两条轨迹在游戏使用的地址上全程一致");
} else {
	const info = diffAt(firstDiff);
	console.log(`\n★ 第一个真正的分岔帧：第 ${firstDiff} 帧（差异 ${info.d} 字节）`);
	for (const [addr, a, b] of info.bytes) {
		console.log(`   $${addr.toString(16).toUpperCase().padStart(4, "0")}: 我=${a.toString(16).toUpperCase().padStart(2, "0")} FCEUX=${b.toString(16).toUpperCase().padStart(2, "0")}`);
	}

	console.log(`\n前后差异数量：`);
	for (let f = Math.max(0, firstDiff - 2); f < Math.min(frames, firstDiff + 30); f++) {
		console.log(`   第 ${f} 帧: ${diffAt(f).d}`);
	}
	// 每隔一段抽样看趋势
	console.log(`\n趋势（每 400 帧）：`);
	for (let f = 0; f < frames; f += 400) {
		console.log(`   第 ${f} 帧: ${diffAt(f).d} / ${live.length}`);
	}
	console.log(`   第 ${frames - 1} 帧: ${diffAt(frames - 1).d} / ${live.length}`);
}
