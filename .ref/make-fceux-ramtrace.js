// 根据 godot-nes 记录的按键文件（play-input.txt：每行"帧号 按键位(hex)"）
// 生成一个 FCEUX Lua 脚本：用同样的时序喂手柄，并逐帧把 2KB 主 RAM 写入二进制文件。
// 之后把两边的 RAM 逐帧对比，第一个分岔的帧和字节就是状态分岔的根因。
//
//   node .ref/make-fceux-ramtrace.js <play-input.txt> <输出.lua> <输出.bin> <总帧数>

import fs from "fs";
import path from "path";

const [, , inputPath, luaPath, binPath, framesStr] = process.argv;
const frames = parseInt(framesStr || "4300", 10);

const entries = [];
for (const line of fs.readFileSync(inputPath, "utf8").split(/\r?\n/)) {
	const p = line.trim().split(/\s+/);
	if (p.length >= 2) entries.push([parseInt(p[0], 10), parseInt(p[1], 16)]);
}
entries.sort((a, b) => a[0] - b[0]);

const table = entries.map(([f, b]) => `{${f},${b}}`).join(",");
const outBin = binPath.replace(/\\/g, "\\\\");

const lua = `-- 由 godot-nes 生成：重放同一段按键并逐帧记录主 RAM（2KB）。
-- 用法：FCEUX → File → Lua → New Lua Script Window… → Browse 选本文件 → Run
-- 跑完会打印 done，并写出 ${path.basename(binPath)}

local entries = {${table}}

local outPath = "${outBin}"
local totalFrames = ${frames}

local file = io.open(outPath, "wb")
if not file then
	print("无法打开输出文件: " .. outPath)
	return
end

local index = 1
local buttons = 0

local function applyButtons(b)
	joypad.set(1, {
		A = (b % 2) >= 1,
		B = (math.floor(b / 2) % 2) >= 1,
		select = (math.floor(b / 4) % 2) >= 1,
		start = (math.floor(b / 8) % 2) >= 1,
		up = (math.floor(b / 16) % 2) >= 1,
		down = (math.floor(b / 32) % 2) >= 1,
		left = (math.floor(b / 64) % 2) >= 1,
		right = (math.floor(b / 128) % 2) >= 1,
	})
end

for frame = 0, totalFrames - 1 do
	while index <= #entries and entries[index][1] <= frame do
		buttons = entries[index][2]
		index = index + 1
	end

	applyButtons(buttons)
	file:write(memory.readbyterange(0, 2048))
	emu.frameadvance()
end

file:close()
print("done: " .. outPath)
`;

fs.writeFileSync(luaPath, lua);
console.log(`已写出 ${luaPath}（${entries.length} 条按键记录，共 ${frames} 帧）`);
console.log(`输出文件：${binPath}`);
