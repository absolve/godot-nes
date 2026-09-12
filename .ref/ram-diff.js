// 比对两份 2KB RAM 镜像，打印不同字节的地址（用来找"选了 2P 之后哪个变量变了"）。
//
//   node .ref/ram-diff.js <a.bin> <b.bin> [标签A] [标签B]

import fs from "fs";

const [, , pathA, pathB, labelA = "A", labelB = "B"] = process.argv;
const a = fs.readFileSync(pathA);
const b = fs.readFileSync(pathB);

if (a.length !== b.length) {
	throw new Error("长度不一致");
}

const addresses = [];
for (let i = 0; i < a.length; i++) {
	if (a[i] !== b[i]) {
		addresses.push({ address: i, from: a[i], to: b[i] });
	}
}

console.log(`${labelA} vs ${labelB}：共 ${addresses.length} 个字节不同`);
const shown = addresses.slice(0, 40);
for (const item of shown) {
	console.log(`  $${item.address.toString(16).toUpperCase().padStart(4, "0")}  ${item.from.toString(16).toUpperCase().padStart(2, "0")} → ${item.to.toString(16).toUpperCase().padStart(2, "0")}`);
}
if (addresses.length > shown.length) {
	console.log(`  …还有 ${addresses.length - shown.length} 个`);
}
console.log("地址集合：" + addresses.map((x) => x.address.toString(16).toUpperCase().padStart(4, "0")).join(" "));
