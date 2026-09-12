// 从 FCEUX 即时存档里提取物理名称表（NTARAM，2KB），和我的名称表对比。
// 底栏那几行是 HUD（与关卡位置无关、静态），所以即使两次运行位置不同也能直接比。
//
//   node .ref/fceux-nametable.js <存档> <我的标记快照.nt>

import fs from "fs";
import zlib from "zlib";

const [, , statePath, myNtPath] = process.argv;

const raw = fs.readFileSync(statePath);
const comprLen = raw.readUInt32LE(12);
const body = raw.subarray(16);
const payload = comprLen === 0xffffffff ? body : zlib.inflateSync(body);
console.log(`payload ${payload.length} 字节`);

// 找段名：FCEUX 的 AddExState 名字（PPU 的物理名称表通常叫 NTARAM）
const names = ["NTARAM", "VRAM", "RAM", "PALRAM", "SPRAM"];
for (const name of names) {
	const hits = [];
	const buf = Buffer.from(name, "latin1");
	for (let i = 0; i <= payload.length - buf.length; i++) {
		if (payload.compare(buf, 0, buf.length, i, i + buf.length) === 0) hits.push(i);
	}
	console.log(`  "${name}": ${hits.length} 处 → 偏移 ${hits.slice(0, 6).join(", ")}`);
}

// 提取 NTARAM：找到名字后，数据在这段结构里。FCEUX 的 chunk 格式是
// [4字节大小][4字节名字长度?][名字][数据]，这里把名字后面 0-24 字节的每个位置
// 都当候选起点，取 2KB，然后看哪一段"像名称表"（前 960 字节里有很多重复的图块号）
const target = "NTARAM";
const buf = Buffer.from(target, "latin1");
let bestNt = null;
for (let i = 0; i <= payload.length - buf.length; i++) {
	if (payload.compare(buf, 0, buf.length, i, i + buf.length) !== 0) continue;
	for (let skip = 0; skip <= 24; skip++) {
		const off = i + buf.length + skip;
		if (off + 2048 > payload.length) continue;
		const nt = payload.subarray(off, off + 2048);
		// 判断像不像名称表：不能整段常数，且要有一定数量的不同值
		const seen = new Set();
		for (let k = 0; k < 2048; k++) seen.add(nt[k]);
		if (seen.size > 8) {
			bestNt = { off, nt, variants: seen.size };
			break;
		}
	}
	if (bestNt) break;
}

if (!bestNt) {
	console.log("没找到可用的 NTARAM 数据");
	process.exit(0);
}
console.log(`\n取到 NTARAM：偏移 ${bestNt.off}，不同字节值 ${bestNt.variants} 种`);

const mine = fs.readFileSync(myNtPath);
console.log(`我的名称表：${mine.length} 字节（4 块逻辑名称表）`);

// 我这边：Horizontal 镜像下 NT0=NT1=物理表A(0-0x3FF)，NT2=NT3=物理表B(0x400-0x7FF)
// 存档里的 NTARAM 就是物理表 A+B 连在一起
console.log(`\n物理表 A（名称表 0 的第一块，含底栏 HUD 所在行）对比：`);
for (const row of [0, 1, 2, 3, 24, 25, 26, 27, 28, 29]) {
	const a = mine.subarray(row * 32, row * 32 + 32);
	const b = bestNt.nt.subarray(row * 32, row * 32 + 32);
	let same = 0;
	for (let k = 0; k < 32; k++) if (a[k] === b[k]) same++;
	const mark = same === 32 ? "完全一致 ✅" : `不同 ${32 - same} 个 ❌`;
	console.log(`   行${String(row).padStart(2)}: ${mark}`);
	if (same !== 32) {
		console.log(`      我  : ${Array.from(a).map((v) => v.toString(16).toUpperCase().padStart(2, "0")).join(" ")}`);
		console.log(`      FCEUX: ${Array.from(b).map((v) => v.toString(16).toUpperCase().padStart(2, "0")).join(" ")}`);
	}
}
