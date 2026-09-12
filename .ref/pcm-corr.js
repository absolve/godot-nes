// 比两份 16 位单声道 PCM 的"响度包络"相关性：
// 两台模拟器的混音音量/低通不一样，绝对值没法直接比，但同一段音乐的形状应该对得上。
//
//   node .ref/pcm-corr.js <a.pcm> <b.pcm> [块大小=441(10ms)]

import fs from "fs";

const [, , pathA, pathB, blockText] = process.argv;
const block = blockText ? parseInt(blockText, 10) : 441;

function envelope(path) {
	const buf = fs.readFileSync(path);
	const count = Math.floor(buf.length / 2);
	const out = [];
	let sum = 0;
	let n = 0;
	for (let i = 0; i < count; i++) {
		const v = buf.readInt16LE(i * 2) / 32768;
		sum += v * v;
		n++;
		if (n === block) {
			out.push(Math.sqrt(sum / n));
			sum = 0;
			n = 0;
		}
	}
	return out;
}

// 过零率：方波里约等于 2 倍基频，可以当成便宜的"音高"估计，
// 用它比对就不受两边混音音量差异影响。
function zeroCrossingRate(path) {
	const buf = fs.readFileSync(path);
	const count = Math.floor(buf.length / 2);
	const out = [];
	let previous = 0;
	let crossings = 0;
	let index = 0;
	let dc = 0;
	for (let i = 0; i < count; i++) {
		const raw = buf.readInt16LE(i * 2) / 32768;
		dc += (raw - dc) * 0.0005;            // 先去掉直流分量
		const v = raw - dc;
		if (i > 0 && ((v >= 0) !== (previous >= 0))) crossings++;
		previous = v;
		index++;
		if (index === block) {
			out.push(crossings);
			crossings = 0;
			index = 0;
		}
	}
	return out;
}

const a = envelope(pathA);
const b = envelope(pathB);
const length = Math.min(a.length, b.length);

function correlation(x, y, lag) {
	let sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, n = 0;
	for (let i = 0; i < length; i++) {
		const j = i + lag;
		if (j < 0 || j >= y.length) continue;
		const xv = x[i];
		const yv = y[j];
		sx += xv; sy += yv; sxx += xv * xv; syy += yv * yv; sxy += xv * yv; n++;
	}
	if (n === 0) return 0;
	const cov = sxy / n - (sx / n) * (sy / n);
	const vx = sxx / n - (sx / n) ** 2;
	const vy = syy / n - (sy / n) ** 2;
	if (vx <= 0 || vy <= 0) return 0;
	return cov / Math.sqrt(vx * vy);
}

let best = { lag: 0, r: -2 };
for (let lag = -120; lag <= 120; lag++) {
	const r = correlation(a, b, lag);
	if (r > best.r) best = { lag, r };
}

const peak = (xs) => xs.reduce((m, v) => Math.max(m, v), 0);
console.log(`块大小 ${block} 个样本（${((block / 44100) * 1000).toFixed(1)} ms），共 ${length} 块`);
console.log(`A 峰值 ${peak(a).toFixed(4)}   B 峰值 ${peak(b).toFixed(4)}`);
console.log(`最佳滞后 ${best.lag} 块（${((best.lag * block) / 44.1).toFixed(1)} ms），相关系数 ${best.r.toFixed(3)}`);
console.log(`滞后 0 的相关系数 ${correlation(a, b, 0).toFixed(3)}`);

// 音高（过零率）比对：这才是"是不是同一段旋律"的硬证据
const za = zeroCrossingRate(pathA);
const zb = zeroCrossingRate(pathB);
const limit = Math.min(za.length, zb.length);
let pitchBest = { lag: 0, r: -2 };
for (let lag = -120; lag <= 120; lag++) {
	const r = correlation(za.slice(0, limit), zb.slice(0, limit), lag);
	if (r > pitchBest.r) pitchBest = { lag, r };
}
console.log(`过零率相关：最佳滞后 ${pitchBest.lag} 块，相关系数 ${pitchBest.r.toFixed(3)}；滞后 0 = ${correlation(za.slice(0, limit), zb.slice(0, limit), 0).toFixed(3)}`);
for (let lag = -6; lag <= 6; lag++) {
	console.log(`  lag ${String(lag).padStart(3)} → ${correlation(a, b, lag).toFixed(3)}`);
}
