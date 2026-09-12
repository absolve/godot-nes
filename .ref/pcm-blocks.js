// 逐块打印 PCM 的 RMS / 峰值 / 过零数，用来判断一段声音是"有起伏的音乐"还是"恒定噪音/单音"。
//
//   node .ref/pcm-blocks.js <a.pcm> [起始块=0] [块数=40] [块大小=4410(100ms)]

import fs from "fs";

const [, , path, fromText, countText, blockText] = process.argv;
const from = fromText ? parseInt(fromText, 10) : 0;
const count = countText ? parseInt(countText, 10) : 40;
const block = blockText ? parseInt(blockText, 10) : 4410;

const buf = fs.readFileSync(path);
const total = Math.floor(buf.length / 2);

console.log(`${path}  ${total} 样本（${(total / 44100).toFixed(2)} 秒），块大小 ${block}`);
console.log("  块      起始s     RMS      峰值   过零次数");
for (let b = from; b < from + count; b++) {
	const start = b * block;
	if (start + block > total) break;
	let sum = 0;
	let peak = 0;
	let crossings = 0;
	let previous = 0;
	let dc = 0;
	for (let i = start; i < start + block; i++) {
		const raw = buf.readInt16LE(i * 2) / 32768;
		dc += (raw - dc) * 0.0005;
		const v = raw - dc;
		sum += v * v;
		if (Math.abs(v) > peak) peak = Math.abs(v);
		if ((v >= 0) !== (previous >= 0)) crossings++;
		previous = v;
	}
	const rms = Math.sqrt(sum / block);
	console.log(
		`  ${String(b).padStart(4)}  ${(start / 44100).toFixed(1).padStart(7)}  ${rms.toFixed(4)}  ${peak.toFixed(4)}  ${String(crossings).padStart(8)}`,
	);
}
