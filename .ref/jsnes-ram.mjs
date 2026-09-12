// 用 jsnes 跑同一段按键脚本，导出 CPU RAM（$0000-$07FF）。
// 用来做"选了 2P 之后游戏状态到底哪里变了"的差分 —— godot-nes 那边用 --dump-ram 0 2048。
//
//   node .ref/jsnes-ram.mjs <rom> <帧数> <输出.bin> [down:150,start:250]

import fs from "fs";

const jsnesPath = "E:/jsnes-main/src/nes.js";
const { default: NES } = await import("file://" + jsnesPath);

const [romPath, framesText, outPath, spec] = process.argv.slice(2);
const frames = parseInt(framesText, 10);

const BUTTONS = {
	a: 0,
	b: 1,
	select: 2,
	start: 3,
	up: 4,
	down: 5,
	left: 6,
	right: 7,
};

const presses = [];
if (spec) {
	for (const item of spec.split(",")) {
		const [name, range] = item.split(":");
		const code = BUTTONS[name.toLowerCase()];
		if (code === undefined) {
			throw new Error(`未知按键 ${name}`);
		}
		const [downText, upText] = range.split("-");
		presses.push({
			code,
			down: parseInt(downText, 10),
			up: upText === undefined ? Infinity : parseInt(upText, 10),
		});
	}
}

const nes = new NES({ emulateSound: false });
nes.loadROM(fs.readFileSync(romPath, "binary"));

for (let i = 0; i < frames; i++) {
	for (const press of presses) {
		if (i === press.down) {
			nes.buttonDown(1, press.code);
		}
		if (i === press.up) {
			nes.buttonUp(1, press.code);
		}
	}
	nes.frame();
}

fs.writeFileSync(outPath, Buffer.from(nes.cpu.mem.slice(0, 0x800)));
console.log(`jsnes：${frames} 帧 → $0000-$07FF 写入 ${outPath}`);
