// 生成 CPU 差分测试用的 "酷刑 ROM"：把官方指令和寻址模式全跑一遍。
//
//   node tests/make_torture_rom.js
//   → tests/roms/torture.nes
//
// 设计要点：
//   1. 指令清单和指令长度是**从参照实现 fogleman/nes 的 cpu.go 里读出来的**，
//      不是从 godot-nes 自己的表来的 —— 否则就是拿自己印证自己。
//   2. 排除会改变 PC 的指令（JMP/JSR/RTS/RTI/BRK/分支），它们放到单独的控制流段落里，
//      否则程序跑不下去。
//   3. 操作数一律指向 RAM（零页 $10、绝对 $0200），避免写到 ROM 或 PPU 寄存器上，
//      这样两台模拟器的内存状态才可比。
//   4. 故意包含跨页的索引寻址：读类指令该多花 1 个周期，写类不该多花 —— 这是最容易写错的地方。

const fs = require("fs");
const path = require("path");

const REF_SRC = "E:/nes-master/nes/cpu.go";
const OUT_DIR = path.join(__dirname, "roms");

// ---------------------------------------------------------------- 读参照的表

const text = fs.readFileSync(REF_SRC, "utf8");

function grabByteArray(name) {
  const m = text.match(new RegExp(`var ${name} = \\[256\\]byte\\{([\\s\\S]*?)\\n\\}`));
  if (!m) throw new Error(`找不到 ${name}`);
  return m[1].split(/[\s,]+/).filter((s) => s).map((s) => parseInt(s, 10));
}

function grabStringArray(name) {
  const m = text.match(new RegExp(`var ${name} = \\[256\\]string\\{([\\s\\S]*?)\\n\\}`));
  if (!m) throw new Error(`找不到 ${name}`);
  return [...m[1].matchAll(/"([A-Z]{3})"/g)].map((x) => x[1]);
}

const refModes = grabByteArray("instructionModes");
const refSizes = grabByteArray("instructionSizes");
const refNames = grabStringArray("instructionNames");

const OFFICIAL = new Set([
  "ADC","AND","ASL","BCC","BCS","BEQ","BIT","BMI","BNE","BPL","BRK","BVC","BVS",
  "CLC","CLD","CLI","CLV","CMP","CPX","CPY","DEC","DEX","DEY","EOR","INC","INX",
  "INY","JMP","JSR","LDA","LDX","LDY","LSR","NOP","ORA","PHA","PHP","PLA","PLP",
  "ROL","ROR","RTI","RTS","SBC","SEC","SED","SEI","STA","STX","STY","TAX","TAY",
  "TSX","TXA","TXS","TYA",
]);

// 会把 PC 指到别处去的指令，不能混在顺序执行的段落里
const CONTROL_FLOW = new Set([
  "JMP","JSR","RTS","RTI","BRK","BCC","BCS","BEQ","BMI","BNE","BPL","BVC","BVS",
]);

const officialOpcodes = [];
for (let op = 0; op < 256; op++) {
  const name = refNames[op];
  // 长度 > 0 才能用：fogleman/nes 对没实现的非法指令留了 0。
  // 这样会顺带带进 NOP 的变体（$1A、$04… 它们本来就该按 NOP 处理），没问题。
  if (OFFICIAL.has(name) && !CONTROL_FLOW.has(name) && refSizes[op] > 0) {
    officialOpcodes.push({ op, name, size: refSizes[op], mode: refModes[op] });
  }
}

// ---------------------------------------------------------------- 迷你汇编器

const BASE = 0x8000; // PRG 映射到 $8000
const code = [];
const labels = {};
const fixups = [];

const addr = () => BASE + code.length;

function emit(...bytes) {
  for (const b of bytes) code.push(b & 0xFF);
}

function label(name) {
  labels[name] = addr();
}

/** 16 位绝对地址 fixup（JSR/JMP 用，操作数低字节在前） */
function emitAbs(opcode, target) {
  const operandPos = code.length + 1; // 注意：pos 指的是**操作数**的位置，别写到操作码上
  emit(opcode, 0, 0);
  fixups.push({ pos: operandPos, kind: "abs", target });
}

/** 相对分支 fixup */
function emitBranch(opcode, target) {
  const operandPos = code.length + 1;
  emit(opcode, 0);
  fixups.push({ pos: operandPos, kind: "rel", target, base: addr() });
}

function padTo(address, fill = 0xEA) {
  while (addr() < address) emit(fill);
  if (addr() !== address) throw new Error(`padTo($${address.toString(16)}) 越过了目标地址`);
}

function padToOffset(offset, fill = 0xEA) {
  while (code.length < offset) emit(fill);
}

// ---------------------------------------------------------------- 组装程序

// 段 A：所有官方非控制流指令按 opcode 升序各跑一遍。
// 2 字节指令的操作数用零页 $10，3 字节指令用 $0200（都在 RAM 里）。
label("start"); // 复位向量指向这里（$8000）
for (const { op, size } of officialOpcodes) {
  if (size === 1) {
    emit(op);
  } else if (size === 2) {
    emit(op, 0x10);
  } else if (size === 3) {
    emit(op, 0x00, 0x02); // $0200
  } else {
    throw new Error(`opcode $${op.toString(16)} 的长度 ${size} 不对`);
  }
}

// 段 B：索引寻址跨页。
// X = Y = $F0，零页指针 $10/$11 = $02F0，于是 $02F0,X → $03E0（跨页），($10),Y 同理。
label("page_cross");
emit(0xA9, 0xF0); // LDA #$F0
emit(0xAA); // TAX
emit(0xA8); // TAY
emit(0xA9, 0xF0); // LDA #$F0
emit(0x85, 0x10); // STA $10
emit(0xA9, 0x02); // LDA #$02
emit(0x85, 0x11); // STA $11    → 指针 = $02F0

const absX = [0xBD, 0xB9, 0x9D, 0xBC, 0xBE, 0xDD, 0xFD, 0x1D, 0x3D, 0x5D, 0x7D, 0xDE, 0xFE, 0x3E, 0x5E, 0x7E];
// 依次是：LDA abs,X / LDA abs,Y / STA abs,X / LDY abs,X / LDX abs,Y /
//         CMP abs,X / SBC abs,X / ORA abs,X / AND abs,X / EOR abs,X / ADC abs,X /
//         DEC abs,X / INC abs,X / ROL abs,X / LSR abs,X / ROR abs,X
for (const op of absX) {
  emit(op, 0xF0, 0x02); // $02F0 + $F0 = $03E0，跨页
}

emit(0xB1, 0x10); // LDA ($10),Y —— 也跨页
emit(0x91, 0x10); // STA ($10),Y —— 写，不该多花周期
emit(0xD1, 0x10); // CMP ($10),Y
emit(0xC1, 0x10); // CMP ($10,X)
emit(0xA1, 0x10); // LDA ($10,X)
emit(0x81, 0x10); // STA ($10,X)

// 段 C：控制流
label("control_flow");

// JSR / RTS
emitAbs(0x20, "subroutine"); // JSR subroutine

// 分支：不跳（Z=0）/ 跳（Z=1）
emit(0xA9, 0x01); // LDA #$01 → Z=0
emitBranch(0xF0, "after_beq_skip"); // BEQ：不该跳
emit(0xEA); // NOP —— 只有不跳才会执行到这里
label("after_beq_skip");

emit(0xA9, 0x00); // LDA #$00 → Z=1
emitBranch(0xF0, "beq_target"); // BEQ：该跳
emit(0xEA); // 被跳过
label("beq_target");

// 反向分支：一个小循环跑 3 次
emit(0xA2, 0x03); // LDX #$03
label("loop");
emit(0xCA); // DEX
emitBranch(0xD0, "loop"); // BNE loop —— 反向跳，会跨回上一页吗？这里没有，但覆盖了反向

// 跨页的分支：垫到「下一页的 $F0」，再往前跳 16 字节 —— 目标落在再下一页，
// 于是这次跳转要多花 1 个周期（跳转 +1、跨页再 +1，共 4 个）。
const branchPage = ((addr() >> 8) + 1) << 8;
padTo(branchPage + 0xF0);
emit(0xA9, 0x01); // LDA #$01 → Z=0
emitBranch(0xD0, "page_cross_target"); // BNE：一定会跳，且跨页
for (let i = 0; i < 16; i++) {
  emit(0xEA); // 被跳过去的填充
}
label("page_cross_target");

// 比较类指令（CMP/CPX/CPY 的立即数形式也顺手覆盖）
emit(0xC9, 0x00); // CMP #$00
emit(0xE0, 0x00); // CPX #$00
emit(0xC0, 0x00); // CPY #$00

// 栈操作 + 传送
emit(0x48, 0x68); // PHA / PLA
emit(0x08, 0x28); // PHP / PLP
emit(0xBA, 0x9A); // TSX / TXS
emit(0x8A, 0xAA); // TXA / TAX
emit(0x98, 0xA8); // TYA / TAY

// BRK：跳到 $F000 的 RTI 处理程序再回来。
// 先把栈指针摆正 —— 前面段 A 里的 TXS 会把 SP 设成当时的 X，SP=0 时 BRK 压栈会绕回，
// RTI 就会弹出垃圾地址（两台模拟器会一起跑飞，测不出后面该测的东西）。
emit(0xA2, 0xFD); // LDX #$FD
emit(0x9A); // TXS
emit(0x00, 0xEA); // BRK + 补齐字节

// JMP 绝对
emitAbs(0x4C, "end");
label("end");
emitAbs(0x4C, "end"); // 自循环，trace 到这里就一直是同一条

// 子程序：覆盖 RTS 和栈
label("subroutine");
emit(0x48); // PHA
emit(0x08); // PHP
emit(0xA9, 0x55); // LDA #$55
emit(0x28); // PLP
emit(0x68); // PLA
emit(0x60); // RTS

// ---------------------------------------------------------------- 中断处理程序

padTo(BASE + 0x7000); // $F000
label("irq_handler");
emit(0x40); // RTI

padTo(BASE + 0x7100); // $F100
label("nmi_handler");
emit(0x40); // RTI

padTo(BASE + 0x7FFA); // $FFFA

// ---------------------------------------------------------------- 回填 fixup

function resolve(name) {
  if (!(name in labels)) throw new Error(`未定义的标签 ${name}`);
  return labels[name];
}

for (const f of fixups) {
  const target = resolve(f.target);
  if (f.kind === "abs") {
    code[f.pos] = target & 0xFF;
    code[f.pos + 1] = (target >> 8) & 0xFF;
  } else {
    const offset = target - f.base;
    if (offset < -128 || offset > 127) throw new Error(`分支 ${f.target} 偏移 ${offset} 超出范围`);
    code[f.pos] = offset & 0xFF;
  }
}

// 向量表
emit(resolve("nmi_handler") & 0xFF, (resolve("nmi_handler") >> 8) & 0xFF); // $FFFA NMI
emit(resolve("start") & 0xFF, (resolve("start") >> 8) & 0xFF); // $FFFC RESET（= $8000）
emit(resolve("irq_handler") & 0xFF, (resolve("irq_handler") >> 8) & 0xFF); // $FFFE IRQ

// ---------------------------------------------------------------- 写 ROM

const PRG_SIZE = 0x8000; // 32KB
const CHR_SIZE = 0x2000; // 8KB

if (code.length !== PRG_SIZE) {
  throw new Error(`PRG 应该正好 ${PRG_SIZE} 字节，实际 ${code.length}`);
}

const header = Buffer.alloc(16);
header.write("NES\x1a", 0, "binary");
header[4] = 2; // 32KB PRG
header[5] = 1; // 8KB CHR
header[6] = 0x00; // 水平镜像、无电池、无 trainer、mapper 低 4 位 = 0
header[7] = 0x00;

const prg = Buffer.from(code);

// CHR：给个可辨认的图案（顺便让 PPU 窗口的 pattern 预览有东西看）
const chr = Buffer.alloc(CHR_SIZE);
for (let tile = 0; tile < CHR_SIZE / 16; tile++) {
  for (let row = 0; row < 8; row++) {
    const base = tile * 16 + row;
    chr[base] = (tile * 8 + row) & 0xFF;
    chr[base + 8] = (((tile >> 2) * 8) + row) & 0xFF;
  }
}

if (!fs.existsSync(OUT_DIR)) fs.mkdirSync(OUT_DIR, { recursive: true });
const outPath = path.join(OUT_DIR, "torture.nes");
fs.writeFileSync(outPath, Buffer.concat([header, prg, chr]));

const traceTarget = addr();
console.log(`已生成 ${outPath}`);
console.log(`  官方非控制流指令 ${officialOpcodes.length} 条，程序共 ${code.length} 字节`);
console.log(`  建议 trace 条数：${1200}（覆盖到自循环即可）`);
