// 生成一个 FCEUX 电影文件（.fm2），内容是 godot-nes 复现"卡死"时用的那条输入脚本：
// 每 150 帧短按一次 Start（10 帧），共 10 次 —— 也就是"狂按 Start 跳文字"。
//
//   node .ref/make-fm2.js <rom路径> <帧数> <输出.fm2>
//
// 用法：FCEUX → File → Play Movie… 选这个文件，看它是不是也会卡在"背景正常、底栏乱码"那一屏。
// 如果 FCEUX 也卡 → 是这个 ROM/游戏的脾气；如果 FCEUX 不卡 → 说明我还有偏差，继续查。
//
// FM2 每行的格式： |commands|port0|port1|port2|port3|
// 端口那 8 个字符依次是 R L D U T S B A（右左下上 Start Select B A）。

const fs = require("fs");
const path = require("path");
const crypto = require("crypto");

const romPath = process.argv[2];
const frames = parseInt(process.argv[3] || "1700", 10);
const outPath = process.argv[4];

const TAP_FRAMES = 10;
const TAP_STARTS = [200, 350, 500, 650, 800, 950, 1100, 1250, 1400, 1550];

function portMask(startPressed) {
	// R L D U T S B A
	return (startPressed ? "." : ".") + "." + "." + "." + (startPressed ? "T" : ".") + "." + "." + ".";
}

const lines = [
	"version 3",
	"emuVersion 22060",
	`romFilename ${path.basename(romPath)}`,
	// FCEUX 的 romChecksum 就是 ROM 整文件 MD5（16 字节）的 base64 —— 填占位值的话
	// FCEUX 会认为电影不是这个 ROM 录的，可能直接拒绝播放，那样"FCEUX 不卡"就说明不了任何问题。
	`romChecksum base64:${crypto.createHash("md5").update(fs.readFileSync(romPath)).digest("base64")}`,
	"guid 00000000-0000-0000-0000-000000000000",
	// 这几行必须写：FCEUX 靠 port0/port1 判断每个口插的是什么设备（1 = 标准手柄）。
	// 缺了它们，FCEUX 会走"不是手柄"的分支去按 Zapper 解析输入行 —— 越界读取，直接闪退。
	"port0 1",
	"port1 1",
	"fourscore 0",
	"comment godot-nes 复现脚本：每 150 帧短按 10 帧 Start",
	"subtitle",
];

for (let frame = 0; frame < frames; frame++) {
	const pressed = TAP_STARTS.some((s) => frame >= s && frame < s + TAP_FRAMES);
	// commands 字段：0=普通帧，1=开机第一帧
	const commands = frame === 0 ? "1" : "0";
	const port = portMask(pressed);
	// FCEUX 的格式（没开 fourscore 时）：|commands|手柄1|手柄2||
	// 注意只有 2 个手柄字段 + 末尾一个空段；多写字段会让 FCEUX 解析出错（实测直接闪退）。
	lines.push(`|${commands}|${port}|........||`);
}

fs.writeFileSync(outPath, lines.join("\n") + "\n");
console.log(`已写出 ${outPath}（${frames} 帧，Start 在 ${TAP_STARTS.join(",")} 各按 ${TAP_FRAMES} 帧）`);
