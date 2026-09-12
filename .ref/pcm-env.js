// 打印 16 位单声道 PCM 的包络时间线（每 100 毫秒一块，0-9 归一化），用来对比两边的"音乐节奏"。
//
//   node .ref/pcm-env.js <a.pcm> <b.pcm>

import fs from "fs";

function envelope(path) {
  const buf = fs.readFileSync(path);
  const count = Math.floor(buf.length / 2);
  const block = 4410; // 100 ms
  const blocks = Math.floor(count / block);
  const rms = [];
  let peak = 0;
  for (let b = 0; b < blocks; b++) {
    let sum = 0;
    for (let i = 0; i < block; i++) {
      const v = buf.readInt16LE((b * block + i) * 2);
      sum += v * v;
      const a = Math.abs(v);
      if (a > peak) peak = a;
    }
    rms.push(Math.sqrt(sum / block));
  }
  return { rms, peak, samples: count };
}

const files = process.argv.slice(2);
const results = files.map(envelope);
const globalPeak = Math.max(...results.map((r) => r.peak));

for (let i = 0; i < files.length; i++) {
  const r = results[i];
  const bar = r.rms
    .map((v) => Math.min(9, Math.round((v / (globalPeak / 9 || 1)) / 1)))
    .join("");
  console.log(`${files[i]}`);
  console.log(`  样本 ${r.samples}  峰值 ${r.peak}  块数 ${r.rms.length}`);
  console.log(`  ${bar}`);
}
