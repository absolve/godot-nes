// 逐帧对比两条 RAM 轨迹（每帧 2KB），找出第一个分岔的帧和具体字节。
// 只比较"至少有一边会发生变化"的地址 —— 未被使用的 RAM 两边初值不同（jsnes 填 FF、
// godot-nes 填 00）但那不是游戏状态，必须排除掉。
//
//   node .ref/ram-trace-diff.js <mine.bin> <theirs.bin> <标签> [起始帧]

import fs from "fs";

const [, , minePath, theirsPath, label, startStr] = process.argv;
const mine = fs.readFileSync(minePath);
const theirs = fs.readFileSync(theirsPath);
const startFrame = startStr ? parseInt(startStr, 10) : 0;

const frameSize = 2048;
const frames = Math.min(Math.floor(mine.length / frameSize), Math.floor(theirs.length / frameSize));

// 只看零页（$0000-$00FF）：游戏变量都在这里。
// 排除栈页（$0100-$01FF，两边执行路径不同时栈内容天然不同，不算状态）
// 以及"jsnes 全程都是 FF"的地址（游戏从没写过）。
const live = [];
for (let i = 0; i < 0x100; i++) {
	let used = false;
	for (let f = 0; f < frames; f++) {
		if (theirs[f * frameSize + i] !== 0xff) { used = true; break; }
	}
	if (used) live.push(i);
}

console.log(`对比 ${frames} 帧；活跃地址（游戏真正使用的）${live.length} / 2048`);

let firstDiffFrame = -1;
const perFrame = [];

for (let f = 0; f < frames; f++) {
	let diffs = 0;
	const bytes = [];
	for (const i of live) {
		const a = mine[f * frameSize + i];
		const b = theirs[f * frameSize + i];
		if (a !== b) {
			diffs++;
			if (bytes.length < 16) bytes.push(`$${i.toString(16).toUpperCase().padStart(4, "0")}: 我=${a.toString(16).toUpperCase().padStart(2, "0")} ${label}=${b.toString(16).toUpperCase().padStart(2, "0")}`);
		}
	}
	perFrame.push(diffs);
	if (diffs > 0 && firstDiffFrame < 0 && f >= startFrame) {
		firstDiffFrame = f;
		console.log(`\n第一个分岔：第 ${f} 帧，${diffs} 个活跃字节不同`);
		for (const b of bytes) console.log(`   ${b}`);
	}
}

if (firstDiffFrame < 0) {
	console.log(`\n第 ${startFrame} 帧起，两条轨迹在活跃地址上完全一致`);
} else {
	console.log(`\n差异数量变化：`);
	const step = Math.max(1, Math.floor((frames - firstDiffFrame) / 10));
	for (let f = firstDiffFrame; f < frames; f += step) {
		console.log(`   第 ${f} 帧: ${perFrame[f]}`);
	}
	console.log(`   第 ${frames - 1} 帧: ${perFrame[frames - 1]} / ${live.length}`);
}
