// jsnes 逐帧 RAM 轨迹：按 godot-nes 记录的按键文件重放，每帧把 2KB 主 RAM 追加写入。
// 之后和 godot-nes 的 --ram-trace 输出逐帧对比，第一个分岔的帧和字节就是线索。
//
//   node .ref/jsnes-ramtrace.mjs <rom> <out.bin> <play-input.txt> <总帧数>

import fs from "fs";
import path from "path";
import { createRequire } from "module";

const require = createRequire(import.meta.url);
const jsnesPath = process.env.JSNES_PATH || "E:/jsnes-main";
const jsnes = require(path.join(jsnesPath, "src/index.js"));

const [, , romPath, outPath, inputPath, framesStr] = process.argv;
const totalFrames = parseInt(framesStr || "4300", 10);

const romData = fs.readFileSync(romPath, "utf8");

// godot-nes 的按键位：bit0=A,1=B,2=Select,3=Start,4=Up,5=Down,6=Left,7=Right
// jsnes 的按键常量见其 Controller；这里直接用数值（A=0,B=1,SELECT=2,START=3,UP=4,DOWN=5,LEFT=6,RIGHT=7）
const entries = [];
for (const line of fs.readFileSync(inputPath, "utf8").split(/\r?\n/)) {
	const p = line.trim().split(/\s+/);
	if (p.length >= 2) entries.push([parseInt(p[0], 10), parseInt(p[1], 16)]);
}
entries.sort((a, b) => a[0] - b[0]);

const nes = new jsnes.NES({
	onFrame: () => {},
	onAudioSample: () => {},
});

nes.loadROM(romData);

const out = fs.openSync(outPath, "w");
let index = 0;
let buttons = 0;
let applied = 0;

for (let frame = 0; frame < totalFrames; frame++) {
	while (index < entries.length && entries[index][0] <= frame) {
		buttons = entries[index][1];
		index++;
	}

	for (let b = 0; b < 8; b++) {
		const mask = 1 << b;
		if (buttons & mask) {
			nes.buttonDown(1, b);
		} else {
			nes.buttonUp(1, b);
		}
	}

	const ram = Buffer.alloc(2048);
	for (let i = 0; i < 2048; i++) ram[i] = nes.cpu.mem[i];
	fs.writeSync(out, ram);

	nes.frame();
	applied++;
}

fs.closeSync(out);
console.log(`jsnes：${applied} 帧 → ${outPath}`);
