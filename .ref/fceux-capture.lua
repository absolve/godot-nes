-- 让 FCEUX 用和 godot-nes 完全相同的输入脚本跑一遍，并在指定帧截图。
--
--   D:\fceux\fceux.exe --loadlua .ref\fceux-capture.lua "E:\Mighty Final Fight (U).nes"
--
-- 输入脚本和 --press 那条一致：每 150 帧短按 10 帧 Start，共 10 次。
-- 截图落在 .ref\fceux-<帧号>.png，用来和 godot-nes 的同帧画面逐像素比。

local TAPS = { 200, 350, 500, 650, 800, 950, 1100, 1250, 1400, 1550 }
local HOLD = 10
local SHOT_FRAMES = { 1640, 2000, 3000 }
local OUT_DIR = "E:\\godot-nes\\.ref\\"

local function pressed(frame)
	for _, start in ipairs(TAPS) do
		if frame > start and frame <= start + HOLD then
			return true
		end
	end
	return false
end

local lastFrame = SHOT_FRAMES[#SHOT_FRAMES]

for frame = 1, lastFrame do
	if pressed(frame) then
		joypad.set(1, { start = true })
	end

	emu.frameadvance()

	for _, shot in ipairs(SHOT_FRAMES) do
		if frame == shot then
			gui.savescreenshotas(OUT_DIR .. "fceux-" .. tostring(shot) .. ".png")
			print("saved frame " .. tostring(shot))
		end
	end
end

print("done")
