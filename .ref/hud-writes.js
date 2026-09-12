// 从 --log-ppu 的日志里还原 $2006/$2007 的写入序列，筛出对名称表前 46 行
// （地址 $2000-$25BF，也就是顶部 HUD + 底栏共用的那段）的写，
// 再和 mark 快照里名称表的实际内容对照 —— 看游戏写了什么、有没有留下来。
//
//   node .ref/hud-writes.js <ppu-log> <mark.nt> [起始帧] [结束帧]

import fs from "fs";

const [, , logPath, ntPath, fromStr, toStr] = process.argv;
const from = fromStr ? parseInt(fromStr, 10) : 0;
const to = toStr ? parseInt(toStr, 10) : Number.MAX_SAFE_INTEGER;

const nt = fs.readFileSync(ntPath);
const lines = fs.readFileSync(logPath, "utf8").split(/\r?\n/);

const regAddr = [];
const regData = [];
for (const line of lines) {
	if (!line) continue;
	const p = line.split(" ");
	const frame = parseInt(p[0], 10);
	if (Number.isNaN(frame) || frame < from || frame > to) continue;
	const scanline = parseInt(p[1], 10);
	const reg = p[2];
	const value = parseInt(p[3], 16);
	if (reg === "0006") regAddr.push({ frame, scanline, value });
	else if (reg === "0007") regData.push({ frame, scanline, value });
}

// 用 $2006 两次写拼出地址，然后跟着的 $2007 依次写
let addrHi = null;
const writes = [];
let addr = 0;
let addrValid = false;
for (const line of lines) {
	if (!line) continue;
	const p = line.split(" ");
	const frame = parseInt(p[0], 10);
	if (Number.isNaN(frame) || frame < from || frame > to) continue;
	const scanline = parseInt(p[1], 10);
	const reg = p[2];
	const value = parseInt(p[3], 16);
	if (reg === "0006") {
		if (addrHi === null) {
			addrHi = value;
		} else {
			addr = ((addrHi & 0x3f) << 8) | value;
			addrHi = null;
			addrValid = true;
			if (addr & 0x3fff) addr &= 0x7fff;
		}
	} else if (reg === "0007" && addrValid) {
		writes.push({ frame, scanline, addr: addr & 0x3fff, value });
		addr = (addr + 1) & 0x7fff;
	}
}

const hud = writes.filter((w) => w.addr >= 0x2000 && w.addr < 0x25c0);
console.log(`一共还原出 ${writes.length} 次 VRAM 写，其中落在名称表前 46 行（$2000-$25BF）的有 ${hud.length} 次`);

if (hud.length) {
	console.log(`\n前 40 次（帧 扫描线 地址 值 | 该地址现在名称表里的值）：`);
	for (const w of hud.slice(0, 40)) {
		const offset = (w.addr - 0x2000) & 0x3ff;
		const now = nt[offset];
		const row = Math.floor(offset / 32);
		const col = offset % 32;
		console.log(
			`   ${w.frame} ${String(w.scanline).padStart(3)} $${w.addr.toString(16).toUpperCase().padStart(4, "0")} ← $${w.value
				.toString(16)
				.toUpperCase()
				.padStart(2, "0")}   行${row} 列${col}  现在=$${now.toString(16).toUpperCase().padStart(2, "0")}${now === w.value ? " (一致)" : " (已被覆盖)"}`
		);
	}

	const covered = hud.filter((w) => nt[(w.addr - 0x2000) & 0x3ff] !== w.value).length;
	console.log(`\n写进去之后被后续写覆盖掉的：${covered} / ${hud.length}`);
}
