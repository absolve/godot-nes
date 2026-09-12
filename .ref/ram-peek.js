// 打印若干 RAM 地址上的值，用来对照不同实现里同一个游戏变量的状态。
//
//   node .ref/ram-peek.js <a.bin> <b.bin> ... -- <地址...>

import fs from "fs";

const argv = process.argv.slice(2);
const split = argv.indexOf("--");
const files = split < 0 ? argv : argv.slice(0, split);
const addresses = (split < 0 ? [] : argv.slice(split + 1)).map((text) => parseInt(text, 16));

for (const file of files) {
	const buf = fs.readFileSync(file);
	const values = addresses.map((address) => {
		const v = buf[address];
		return `${address.toString(16).toUpperCase().padStart(4, "0")}=${(v ?? -1).toString(16).toUpperCase().padStart(2, "0")}`;
	});
	console.log(`${file.padEnd(34)} ${values.join("  ")}`);
}
