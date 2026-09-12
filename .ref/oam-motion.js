// 分析 --log-sprites 的输出：判断游戏有没有"在动"（= 进入关卡），
// 并给出最后一帧的 OAM 全貌。
//
//   node .ref/oam-motion.js <play-spr.txt>

import fs from "fs";

const path = process.argv[2];
const lines = fs.readFileSync(path, "utf8").split(/\r?\n/);

const oamByFrame = new Map();
const lineStats = new Map();

for (const line of lines) {
	if (!line) continue;
	const parts = line.split(" ");
	const frame = parseInt(parts[0], 10);
	if (Number.isNaN(frame)) continue;

	if (parts[1] && parts[1].startsWith("OAM")) {
		if (!oamByFrame.has(frame)) oamByFrame.set(frame, []);
		oamByFrame.get(frame).push(parts.slice(2).join(" "));
	} else if (line.includes("line=")) {
		if (!lineStats.has(frame)) lineStats.set(frame, []);
		lineStats.get(frame).push(parts.slice(1).join(" "));
	}
}

const frames = [...oamByFrame.keys()].sort((a, b) => a - b);
console.log(`日志覆盖帧号: ${frames.length ? frames[0] + " ~ " + frames[frames.length - 1] : "(没有 OAM 记录)"}`);

// 逐帧比较 OAM 内容，统计"变动帧"（= 精灵在动 = 进了关卡或者有动画）
let changes = 0;
let firstChange = -1;
let prev = null;
const changeFrames = [];
for (const f of frames) {
	const cur = oamByFrame.get(f).join("|");
	if (prev !== null && cur !== prev) {
		changes++;
		if (firstChange < 0) firstChange = f;
		if (changeFrames.length < 12) changeFrames.push(f);
	}
	prev = cur;
}
console.log(`OAM 变动次数: ${changes}（第一次变动在第 ${firstChange} 帧）`);
if (changeFrames.length) console.log(`变动帧样例: ${changeFrames.join(", ")}`);

if (frames.length) {
	const last = frames[frames.length - 1];
	console.log(`\n最后一帧（第 ${last} 帧）的 OAM：`);
	for (const s of oamByFrame.get(last)) console.log("   " + s);
	console.log(`\n该帧的扫描线统计：`);
	for (const s of lineStats.get(last) || []) console.log("   " + s);
}
