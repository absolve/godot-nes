// 解析 FCEUX 的即时存档（FCSX 格式：16 字节头 + zlib 压缩的段数据），
// 取出里面的 2KB 主 RAM，然后去 godot-nes 的逐帧 RAM 轨迹里搜索"最接近的一帧"。
//
//   node .ref/fceux-state-parse.js <存档文件> <我的轨迹.bin>

import fs from "fs";
import zlib from "zlib";

const [, , statePath, tracePath] = process.argv;

const raw = fs.readFileSync(statePath);
console.log(`存档 ${statePath}：${raw.length} 字节`);

if (raw.subarray(0, 4).toString("latin1") !== "FCSX") {
	console.log(`头 4 字节是 "${raw.subarray(0, 4).toString("latin1")}"，不是 FCSX —— 可能是旧格式，先按未压缩处理`);
}

const totalSize = raw.readUInt32LE(4);
const version = raw.readUInt32LE(8);
const comprLen = raw.readUInt32LE(12);
console.log(`totalsize=${totalSize} version=${version} comprlen=0x${comprLen.toString(16)}`);

let payload;
const body = raw.subarray(16);
// comprlen == 0xFFFFFFFF 表示未压缩；否则 body 就是 zlib 压缩数据
// （注意：压缩后的长度恰好等于 body 长度，所以不能用长度相等来判断未压缩）
if (comprLen === 0xffffffff) {
	payload = body;
	console.log("未压缩（按原样使用 payload）");
} else {
	try {
		payload = zlib.inflateSync(body);
		console.log(`zlib 解压成功：${body.length} → ${payload.length} 字节`);
	} catch (e) {
		console.log(`zlib 解压失败（${e.message}），改用原始 payload`);
		payload = body;
	}
}

// 在 payload 里找 "RAM" 段
const candidates = [];
for (let i = 0; i < payload.length - 4; i++) {
	if (payload[i] === 0x52 && payload[i + 1] === 0x41 && payload[i + 2] === 0x4d) {
		// "RAM"，后面可能直接跟数据，也可能是 0 结尾
		if (payload[i + 3] === 0x00 || payload[i + 3] === 0x10 || payload[i + 3] === 0x08) {
			candidates.push(i);
		}
	}
}
console.log(`payload 里找到 ${candidates.length} 处可能的 "RAM" 标记`);

if (!fs.existsSync(tracePath)) {
	console.log(`（没有 ${tracePath}，只打印候选偏移）`);
	for (const c of candidates.slice(0, 8)) console.log(`   偏移 ${c}: 后面 16 字节 = ${payload.subarray(c, c + 16).toString("hex")}`);
	process.exit(0);
}

const trace = fs.readFileSync(tracePath);
const frameSize = 2048;
const frames = Math.floor(trace.length / frameSize);

// 对每个候选位置 × 每个可能的起点偏移 × 每一帧，算匹配字节数。
// 关键：先排除"整段常数值"的候选（存档里有大段填充/全零区，会假阳性命中我的第 0 帧）。
let best = { offset: -1, frame: -1, match: -1 };
const skips = [];
for (let s = 0; s <= 24; s++) skips.push(s);
skips.push(2048);
for (const c of candidates) {
	for (const skip of skips) {
		const off = c + skip;
		if (off + frameSize > payload.length) continue;
		const ram = payload.subarray(off, off + frameSize);

		// 零页必须像"真实游戏状态"：至少 16 个非零字节，且不能整段都是同一个值
		let nonzero = 0;
		for (let i = 0; i < 0x100; i++) if (ram[i] !== 0) nonzero++;
		if (nonzero < 16) continue;
		let constant = true;
		for (let i = 1; i < frameSize; i++) if (ram[i] !== ram[0]) { constant = false; break; }
		if (constant) continue;

		for (let f = 1; f < frames; f++) {
			let match = 0;
			const base = f * frameSize;
			for (let i = 0; i < frameSize; i++) {
				if (trace[base + i] === ram[i]) match++;
			}
			if (match > best.match) best = { offset: off, frame: f, match };
		}
	}
}

console.log(`\n最佳匹配：存档偏移 ${best.offset} ↔ 我的第 ${best.frame} 帧，2048 字节里相同 ${best.match} 个（${((best.match / 2048) * 100).toFixed(1)}%）`);

// 打印最佳匹配下的差异
const ram = payload.subarray(best.offset, best.offset + frameSize);
const base = best.frame * frameSize;
let shown = 0;
console.log("\n零页差异（前 24 个）：");
for (let i = 0; i < 0x100 && shown < 24; i++) {
	if (trace[base + i] !== ram[i]) {
		console.log(`   $${i.toString(16).toUpperCase().padStart(4, "0")}: 我=${trace[base + i].toString(16).toUpperCase().padStart(2, "0")} FCEUX=${ram[i].toString(16).toUpperCase().padStart(2, "0")}`);
		shown++;
	}
}
const outRam = "E:/godot-nes/.ref/fceux-ram-extracted.bin";
fs.writeFileSync(outRam, ram);
console.log(`\n已把 FCEUX 的 2KB RAM 写出到 ${outRam}`);
