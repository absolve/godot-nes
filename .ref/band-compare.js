// 把两个索引帧的"底部状态栏区（y=192..239）"抽出来，上下叠成一张 256x240 的帧，方便直接肉眼对比。
//
//   node .ref/band-compare.js <a.idx> <b.idx> <输出.idx>

import fs from "fs";

const W = 256;
const H = 240;
const BAND_TOP = 192;
const BAND_H = H - BAND_TOP;   // 48

const [, , pathA, pathB, outPath] = process.argv;
const a = fs.readFileSync(pathA);
const b = fs.readFileSync(pathB);

const out = Buffer.alloc(W * H, 0x0f);   // 背景先填成 NES 调色板的 $0F（黑）

for (let y = 0; y < BAND_H; y++) {
	for (let x = 0; x < W; x++) {
		out[y * W + x] = a[(BAND_TOP + y) * W + x];                       // 上半：A 的底部条
		out[(BAND_H + y) * W + x] = b[(BAND_TOP + y) * W + x];           // 下半：B 的底部条
	}
}

fs.writeFileSync(outPath, out);
console.log(`已写出 ${outPath}（上=A 的 y192-239，下=B 的 y192-239）`);
