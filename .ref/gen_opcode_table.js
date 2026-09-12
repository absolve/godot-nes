// 从 fogleman/nes 的 cpu.go 里把 opcode 矩阵（操作 / 寻址模式 / 周期）抽出来，
// 生成 godot-nes 的 C# opcode 表，避免手抄 256 项出错。
//
//   node .ref/gen_opcode_table.js > godot-nes/core/CPU/OpcodeTable.cs
//
// fogleman/nes 的表本身就是 6502 官方矩阵的 16x16 排布，跟 nesdev 的表一致。

const fs = require("fs");

const SRC = "E:/nes-master/nes/cpu.go";
const text = fs.readFileSync(SRC, "utf8");

/** 从 Go 源码里抓出一个 [256]byte 数组的字面量内容 */
function grabByteArray(name) {
  const re = new RegExp(`var ${name} = \\[256\\]byte\\{([\\s\\S]*?)\\n\\}`);
  const m = text.match(re);
  if (!m) throw new Error(`找不到 ${name}`);
  return m[1]
    .split(/[\s,]+/)
    .filter((s) => s.length > 0)
    .map((s) => parseInt(s, 10));
}

/** 抓 [256]string 数组（指令名） */
function grabStringArray(name) {
  const re = new RegExp(`var ${name} = \\[256\\]string\\{([\\s\\S]*?)\\n\\}`);
  const m = text.match(re);
  if (!m) throw new Error(`找不到 ${name}`);
  return [...m[1].matchAll(/"([A-Z]{3})"/g)].map((x) => x[1]);
}

const modesGo = grabByteArray("instructionModes");
const cyclesGo = grabByteArray("instructionCycles");
const namesGo = grabStringArray("instructionNames");

// Go 的数字模式 → 我的枚举名
const MODE = {
  1: "Abs", 2: "AbsX", 3: "AbsY", 4: "Acc", 5: "Imm", 6: "Imp",
  7: "IndX", 8: "Ind", 9: "IndY", 10: "Rel", 11: "Zp", 12: "ZpX", 13: "ZpY",
};

// 官方 56 条指令，名字直接对上
const OFFICIAL = new Set([
  "ADC","AND","ASL","BCC","BCS","BEQ","BIT","BMI","BNE","BPL","BRK","BVC","BVS",
  "CLC","CLD","CLI","CLV","CMP","CPX","CPY","DEC","DEX","DEY","EOR","INC","INX",
  "INY","JMP","JSR","LDA","LDX","LDY","LSR","NOP","ORA","PHA","PHP","PLA","PLP",
  "ROL","ROR","RTI","RTS","SBC","SEC","SED","SEI","STA","STX","STY","TAX","TAY",
  "TSX","TXA","TXS","TYA",
]);

// 非法指令里的那些 "NOP" 其实就是官方的 NOP 变体，按 NOP 处理更兼容（很多游戏会用）
const isIllegalNop = (name) => name === "NOP";

const rows = [];
const stats = { official: 0, nopVariant: 0, illegal: 0 };

for (let row = 0; row < 16; row++) {
  const cells = [];
  for (let col = 0; col < 16; col++) {
    const opcode = row * 16 + col;
    const name = namesGo[opcode];
    const mode = MODE[modesGo[opcode]];
    const cycles = cyclesGo[opcode];
    if (!mode) throw new Error(`opcode $${opcode.toString(16)} 的模式 ${modesGo[opcode]} 不认识`);

    let ins;
    if (OFFICIAL.has(name)) {
      ins = name[0] + name.slice(1).toLowerCase();
      stats.official++;
    } else if (isIllegalNop(name)) {
      ins = "Nop";
      stats.nopVariant++;
    } else {
      ins = "Illegal";
      stats.illegal++;
    }

    cells.push(`new(Instruction.${ins}, AddrMode.${mode}, ${cycles})`);
  }
  rows.push(
    `        // $${(row * 16).toString(16).toUpperCase().padStart(2, "0")}-$${(
      row * 16 + 15
    ).toString(16).toUpperCase().padStart(2, "0")}\n        ` +
      cells.join(", ") +
      ","
  );
}

const out = `// 本文件由 .ref/gen_opcode_table.js 从 fogleman/nes 的 cpu.go 生成，不要手改。
//
// 6502 的 opcode 矩阵：每行 16 个操作码，取值 = 操作 + 寻址模式 + 基础周期数。
// 基础周期数**不含**跨页附加周期（那部分在 Cpu.ResolveAddress 里按需加）。
//
// 三个来源互相核对过：fogleman/nes 的 instructionModes/instructionCycles、
// jsnes 的 OPCODE_TABLE、以及 nesdev 的官方指令表。
// 非官方指令里那些 "NOP" 变体按 NOP 处理（很多游戏会用它们），其余标成 Illegal。

namespace GodotNes.Core;

public static class OpcodeTable
{
    /// <summary>256 项 opcode 定义，索引就是操作码本身。</summary>
    public static readonly Op[] Ops =
    {
${rows.join("\n")}
    };

    /// <summary>取某个操作码的定义。</summary>
    public static ref readonly Op Get(byte opcode) => ref Ops[opcode];
}
`;

process.stdout.write(out);
process.stderr.write(
  `官方 ${stats.official} + NOP 变体 ${stats.nopVariant} + 非法 ${stats.illegal} = ${
    stats.official + stats.nopVariant + stats.illegal
  }\n`
);
