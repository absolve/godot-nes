// 一次性脚本：比较两条 CPU trace，能容忍"同一段代码错开几行"（比如 NMI 早到几个周期）。
//
//   node .ref/tracediff.js <a.txt> <b.txt>
//
// 做法：把 b 里每个位置的 20 行窗口做成哈希表；从头走 a，遇到不一致就跳到下一个
// "两边都有同一段 20 行窗口"的位置。这样只会报出**真正跑了不同代码**的地方。

const fs = require("fs");

const WINDOW = 12;
const MAX_SKIP = 40000;

// 注意：godot-nes 的 StreamWriter 写的是 CRLF，Go 那边是 LF —— 统一按 \r?\n 切，免得
// 每一行都"看起来不一样"（踩过：第一版脚本在第 1 行就说对不上，其实只差一个 \r）。
//
// 另外**比较时把最后的 CYC 列去掉**：NMI 早到几个周期、或者像我这样把 DMA 停顿算进
// 指令周期里，同一条指令的周期数就会永远差几，那样任何一段窗口都对不上、重同步失效。
// 周期数本身由 cpu_diff.ps1 单独保证。
const read = (p) => fs
  .readFileSync(p, "utf8")
  .split(/\r?\n/)
  .map((line) => {
    const parts = line.split(" ");
    return parts.length === 10 ? parts.slice(0, 9).join(" ") : line;
  });

const a = read(process.argv[2]);
const b = read(process.argv[3]);

// 用两个 32 位 FNV 拼成 key：整窗口字符串当 key 在百万行级别会吃掉几百 MB
function hashWindow(lines, start) {
  let h1 = 0x811c9dc5;
  let h2 = 0x01000193;
  for (let k = 0; k < WINDOW; k++) {
    const line = lines[start + k] || "";
    for (let i = 0; i < line.length; i++) {
      const c = line.charCodeAt(i);
      h1 = Math.imul(h1 ^ c, 16777619) >>> 0;
      h2 = Math.imul(h2 + c + k, 2246822519) >>> 0;
    }
    h1 = Math.imul(h1 ^ 0x9e3779b9, 16777619) >>> 0;
  }
  return h1.toString(16) + "-" + h2.toString(16);
}

const map = new Map();
for (let j = 0; j + WINDOW <= b.length; j++) {
  const key = hashWindow(b, j);
  if (!map.has(key)) map.set(key, []);
  map.get(key).push(j);
}

let i = 0;
let j = 0;
let events = 0;
const maxEvents = 12;

while (i < a.length && j < b.length) {
  if (a[i] === b[j]) {
    i++;
    j++;
    continue;
  }

  // 找重同步：从 a 的 i 出发，往后找一个位置 i2，使得 a[i2..i2+20) 在 b 的 j 之后出现过
  let resync = null;
  for (let k = 0; k < MAX_SKIP && i + k + WINDOW <= a.length; k++) {
    const key = hashWindow(a, i + k);
    const candidates = map.get(key);
    if (!candidates) continue;
    for (const cand of candidates) {
      if (cand >= j) {
        resync = { i: i + k, j: cand };
        break;
      }
    }
    if (resync) break;
  }

  if (!resync) {
    console.log(`第 ${i + 1} 行之后再也对不上（a 剩 ${a.length - i} 行，b 剩 ${b.length - j} 行）`);
    console.log("  a: " + a[i]);
    console.log("  b: " + b[j]);
    break;
  }

  events++;
  if (events <= maxEvents) {
    console.log(`分歧 #${events}：a 第 ${i + 1} 行 / b 第 ${j + 1} 行  →  重同步到 a 第 ${resync.i + 1} 行 / b 第 ${resync.j + 1} 行` +
      `（a 跳过 ${resync.i - i} 行，b 跳过 ${resync.j - j} 行）`);
    for (let k = 0; k < Math.min(3, resync.i - i || 1); k++) {
      console.log(`    a[${i + k + 1}] ${a[i + k]}`);
    }
    for (let k = 0; k < Math.min(3, resync.j - j || 1); k++) {
      console.log(`    b[${j + k + 1}] ${b[j + k]}`);
    }
  }

  i = resync.i;
  j = resync.j;
}

console.log(`共 ${events} 处分歧；a 走了 ${i}/${a.length} 行，b 走了 ${j}/${b.length} 行`);
