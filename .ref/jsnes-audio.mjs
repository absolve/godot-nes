// 用 jsnes 把前 N 帧的音频导出来（单声道 16 位 PCM），和 godot-nes 的 --dump-audio 比。
//
//   node .ref/jsnes-audio.mjs <rom> <帧数> <输出.pcm> [按下 Start 的帧号]

import fs from "fs";

const jsnesPath = "E:/jsnes-main/src/nes.js";
const { default: NES } = await import("file://" + jsnesPath);

const [romPath, framesText, outPath, startText] = process.argv.slice(2);
const frames = parseInt(framesText, 10);
// 给了帧号就一直按着 Start（用来跳过标题画面，听关卡音乐）
const startFrame = startText === undefined ? -1 : parseInt(startText, 10);

const rom = fs.readFileSync(romPath, "binary");
const samples = [];

const nes = new NES({
  emulateSound: true,
  sampleRate: 44100,
  onAudioSample: (left) => {
    samples.push(left);
  },
});

nes.loadROM(rom);
for (let i = 0; i < frames; i++) {
  if (i === startFrame) {
    nes.buttonDown(1, 3 /* BUTTON_START */);
  }
  nes.frame();
}

const pcm = Buffer.alloc(samples.length * 2);
for (let i = 0; i < samples.length; i++) {
  let v = Math.max(-1, Math.min(1, samples[i]));
  v = Math.round(v * 32767);
  pcm.writeInt16LE(v, i * 2);
}

fs.writeFileSync(outPath, pcm);
console.log(`jsnes：${frames} 帧 → ${samples.length} 个样本（${(samples.length / 44100).toFixed(2)} 秒）${outPath}`);
