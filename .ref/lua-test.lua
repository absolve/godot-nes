-- 最小自检脚本
local f = io.open("E:\\godot-nes\\.ref\\lua-test.txt", "w")
if f then
	f:write("lua-ok\n")
	f:close()
else
	-- io 不可用时，用 print 让控制台留痕
	print("io unavailable")
end
print("script-loaded")