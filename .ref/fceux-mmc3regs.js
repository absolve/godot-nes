// 从 FCEUX 存档里找出各个段名，并读出 MMC3 的 8 个寄存器（DRegBuf）——
// 其中的 R0/R1 决定 BG 图案表 $0000-$0FFF 用哪个 CHR bank。
//
//   node .ref/fceux-mmc3regs.js <存档>

import fs from "fs";
import zlib from "zlib";

const [, , statePath] = process.argv;
const raw = fs.readFileSync(statePath);
const comprLen = raw.readUInt32LE(12);
const body = raw.subarray(16);
const payload = comprLen === 0xffffffff ? body : zlib.inflateSync(body);

// 列出 payload 里的短 ASCII 段名（4-12 个可打印字符，两侧是非可打印字节）
const names = [];
for (let i = 0; i < payload.length - 4; i++) {
	let len = 0;
	while (len < 16 && i + len < payload.length) {
		const c = payload[i + len];
		if (c >= 0x41 && c <= 0x5a) len++;
		else break;
	}
	if (len >= 3 && len <= 12) {
		const before = i > 0 ? payload[i - 1] : 0;
		const after = payload[i + len] ?? 0;
		if (!(before >= 0x30 && before <= 0x7a) && !(after >= 0x30 && after <= 0x7a)) {
			names.push({ off: i, name: payload.subarray(i, i + len).toString("latin1") });
		}
	}
}
console.log(`找到 ${names.length} 个候选段名：`);
console.log("   " + names.map((n) => `${n.name}@${n.off}`).join("  "));

// 找 MMC3 的寄存器段
for (const cand of ["DREG", "DReg", "REGS", "MMC3"]) {
	const hit = names.find((n) => n.name === cand);
	if (!hit) continue;
	console.log(`\n段 "${cand}" 在偏移 ${hit.off}`);
	// 名字后面通常是：若干个填充/长度字节，然后是数据。把 0-24 的每个起点都打印出来
	for (let skip = 0; skip <= 16; skip++) {
		const off = hit.off + cand.length + skip;
		if (off + 8 > payload.length) break;
		const regs = Array.from(payload.subarray(off, off + 8));
		console.log(`   +${String(skip).padStart(2)} → ${regs.map((v) => "$" + v.toString(16).toUpperCase().padStart(2, "0")).join(" ")}   (R0=$${regs[0].toString(16).toUpperCase()} R1=$${regs[1].toString(16).toUpperCase()})`);
	}
}
