// 用 jsnes（另一份独立实现）跑同一个 ROM，把第 N 帧写成 PNG，作为"第三方意见"。
//
//   node .ref/jsnes-frame.mjs <rom> <帧数> <输出.png>
//
// 为什么要它：godot-nes 和 fogleman/nes 在某块 ROM 上画面不一致时，得有个第三方来判断
// 到底谁对。jsnes 是纯 JS、能直接 headless 跑，正好当这个裁判。

import fs from "fs";
import zlib from "zlib";

const jsnesPath = "E:/jsnes-main/src/nes.js";
const { default: NES } = await import("file://" + jsnesPath);

const [romPath, framesText, outPath, spec] = process.argv.slice(2);
const frames = parseInt(framesText, 10);

// 可选按键脚本，格式和 jsnes-ram.mjs 一样：start:250 / down:150-220,start:250
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

const rom = fs.readFileSync(romPath, "binary");

let buffer = null;
// 注意：jsnes 在构造时就把 opts.onFrame 拷进 writeFrame()，构造之后再改 opts 是没用的
const nes = new NES({
  emulateSound: false,
  onFrame: (frameBuffer) => {
    buffer = frameBuffer;
  },
});

nes.loadROM(rom);
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

if (!buffer) {
  console.error("没拿到帧");
  process.exit(1);
}

// jsnes 的 framebuffer 是 Uint32Array(256*240)，每个元素是 0xRRGGBB（不是字节数组）
const W = 256;
const H = 240;
const raw = Buffer.alloc(H * (1 + W * 4));
for (let y = 0; y < H; y++) {
  raw[y * (1 + W * 4)] = 0;
  for (let x = 0; x < W; x++) {
    const v = buffer[y * W + x];
    const dst = y * (1 + W * 4) + 1 + x * 4;
    raw[dst] = (v >> 16) & 0xff;
    raw[dst + 1] = (v >> 8) & 0xff;
    raw[dst + 2] = v & 0xff;
    raw[dst + 3] = 255;
  }
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const body = Buffer.concat([Buffer.from(type, "ascii"), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body) >>> 0);
  return Buffer.concat([len, body, crc]);
}

let crcTable = null;
function crc32(buf) {
  if (!crcTable) {
    crcTable = [];
    for (let n = 0; n < 256; n++) {
      let c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      crcTable[n] = c;
    }
  }
  let c = 0xffffffff;
  for (const b of buf) c = crcTable[(c ^ b) & 0xff] ^ (c >>> 8);
  return c ^ 0xffffffff;
}

const ihdr = Buffer.alloc(13);
ihdr.writeUInt32BE(W, 0);
ihdr.writeUInt32BE(H, 4);
ihdr[8] = 8;    // bit depth
ihdr[9] = 6;    // RGBA
const png = Buffer.concat([
  Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
  chunk("IHDR", ihdr),
  chunk("IDAT", zlib.deflateSync(raw)),
  chunk("IEND", Buffer.alloc(0)),
]);

fs.writeFileSync(outPath, png);
console.log(`jsnes：第 ${frames} 帧已写出 ${outPath}`);
