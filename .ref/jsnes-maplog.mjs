// 用 jsnes 跑同一段按键脚本，把映射器寄存器写记成 "地址 值"（和 godot-nes 的 --log-mapper 对比）。
//
//   node .ref/jsnes-maplog.mjs <rom> <帧数> <输出.txt> [按键脚本]

import fs from "fs";

const jsnesPath = "E:/jsnes-main/src/nes.js";
const { default: NES } = await import("file://" + jsnesPath);

const [romPath, framesText, outPath, spec] = process.argv.slice(2);
const frames = parseInt(framesText, 10);

const BUTTONS = { a: 0, b: 1, select: 2, start: 3, up: 4, down: 5, left: 6, right: 7 };
const presses = [];
if (spec) {
	for (const item of spec.split(",")) {
		const [name, range] = item.split(":");
		const [downText, upText] = range.split("-");
		presses.push({
			code: BUTTONS[name.toLowerCase()],
			down: parseInt(downText, 10),
			up: upText === undefined ? Infinity : parseInt(upText, 10),
		});
	}
}

const nes = new NES({ emulateSound: false });
nes.loadROM(fs.readFileSync(romPath, "binary"));

// 挂日志：jsnes 的映射器实例是 nes.mmap，它的 write(address, value) 处理 $8000+ 的寄存器写
const mapper = nes.mmap;
const log = [];
const originalWrite = mapper.write.bind(mapper);
mapper.write = (address, value) => {
	if (address >= 0x8000) {
		log.push(`${address.toString(16).toUpperCase().padStart(4, "0")} ${value.toString(16).toUpperCase().padStart(2, "0")}`);
	}
	return originalWrite(address, value);
};

for (let i = 0; i < frames; i++) {
	for (const press of presses) {
		if (i === press.down) nes.buttonDown(1, press.code);
		if (i === press.up) nes.buttonUp(1, press.code);
	}
	nes.frame();
}

fs.writeFileSync(outPath, log.join("\n") + "\n");
console.log(`jsnes：${frames} 帧 → ${log.length} 条映射器写 → ${outPath}`);
