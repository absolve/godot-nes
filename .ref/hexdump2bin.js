// 把 godot-nes `--dump-ram` 打印的那行十六进制转成二进制，方便和 jsnes 的 RAM 镜像逐字节比。
//
//   node .ref/hexdump2bin.js <dump.txt> <out.bin>

import fs from "fs";

const [, , input, output] = process.argv;
const text = fs.readFileSync(input, "utf8");
const colon = text.indexOf(":");
if (colon < 0) {
	throw new Error("找不到冒号，格式不对");
}
const bytes = text
	.slice(colon + 1)
	.trim()
	.split(/\s+/)
	.map((token) => parseInt(token, 16));

if (bytes.some((v) => Number.isNaN(v))) {
	throw new Error("有解析不出来的字节");
}

fs.writeFileSync(output, Buffer.from(bytes));
console.log(`${bytes.length} 字节 → ${output}`);
