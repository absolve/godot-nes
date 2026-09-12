// 生成 mapper 测试 ROM（mapper 1/2/3/4/7）。
//
//   node tests/make_mapper_test_rom.js
//   → tests/roms/mapper1.nes … mapper7.nes
//
// 每一块 ROM 都是**真正的 6502 程序**，运行时真的去写 mapper 的换 bank 寄存器，
// 然后把"换完之后从窗口里读回来的字节"和手算的期望值逐个比对：
//
//   * 每一块 PRG bank 的开头放一个特征字节（16KB bank k → $10+k，8KB → $20+k，32KB → $30+k）；
//   * 程序从窗口读这个字节，和手算的期望值比；
//   * 不等就 INC $03F0（失败计数），同时把**实际读到的值**记在 $0310+X；
//   * 跑完把测试条数写到 $03F1、完成标志 $A5 写到 $03F2。
//
// 这样一份 ROM 有两种用法（两种都要跑，缺一不可）：
//   1. 和参照实现（fogleman/nes）做 CPU trace 差分 —— 期望值错了会走不同分支，
//      两条 trace 立刻对不上，能抓住"两边一起错"以外的任何偏差；
//   2. 用 --dump-ram 03F0 20 直接看失败计数和实际值 —— 期望值是手算的，
//      能抓住"和参照实现错得一模一样"的情况。
//
// 镜像（单屏/水平/垂直）也这么测：往 $2000 写一个值，再从别的名称表窗口读回来 ——
// 走 $2006/$2007，CPU 侧就能看见镜像到底怎么映射的。

const fs = require("fs");
const path = require("path");

const OUT_DIR = path.join(__dirname, "roms");

// ---------------------------------------------------------------- 迷你汇编器

/**
 * 迷你汇编器。base 是"这段代码将来会被放在哪个 CPU 地址"——
 * 标签要按绝对地址算，否则 JSR/JMP 的 16 位操作数会写成文件内的偏移（踩过一次）。
 * 分支是相对寻址，不受影响，但也统一用绝对标签算差值。
 */
function createAssembler(base) {
  const code = [];
  const labels = {};
  const fixups = [];

  const addr = () => base + code.length;

  function emit(...bytes) {
    for (const b of bytes) code.push(b & 0xFF);
  }

  function label(name) {
    labels[name] = addr();
  }

  // 16 位绝对地址（低字节在前）。target 可以是标签名，也可以是数字（绝对 CPU 地址）。
  function abs(opcode, target) {
    const pos = code.length + 1;
    emit(opcode, 0, 0);
    if (typeof target === "number") {
      code[pos] = target & 0xFF;
      code[pos + 1] = (target >> 8) & 0xFF;
    } else {
      fixups.push({ pos, target });
    }
  }

  function branch(opcode, target) {
    const pos = code.length + 1;
    emit(opcode, 0);
    fixups.push({ pos, target, relative: true, next: base + code.length });
  }

  function resolve() {
    for (const f of fixups) {
      const target = typeof f.target === "number" ? f.target : labels[f.target];
      if (target === undefined) throw new Error(`未定义的标签 ${f.target}`);
      if (f.relative) {
        const offset = target - f.next;
        if (offset < -128 || offset > 127) throw new Error(`分支 ${f.target} 偏移越界`);
        code[f.pos] = offset & 0xFF;
      } else {
        code[f.pos] = target & 0xFF;
        code[f.pos + 1] = (target >> 8) & 0xFF;
      }
    }
    fixups.length = 0;
  }

  return {
    code, labels, emit, label, abs, branch, resolve,
    ldaImm: (v) => emit(0xA9, v & 0xFF),
    ldxImm: (v) => emit(0xA2, v & 0xFF),
    ldaAbs: (a) => abs(0xAD, a),
    staAbs: (a) => abs(0x8D, a),
    staAbsX: (a) => abs(0x9D, a),
    cmpAbs: (a) => abs(0xCD, a),
    incAbs: (a) => abs(0xEE, a),
    jsr: (t) => abs(0x20, t),
    jmp: (t) => abs(0x4C, t),
    beq: (t) => branch(0xF0, t),
    rts: () => emit(0x60),
  };
}

// ---------------------------------------------------------------- 公共代码片段

/** 复位后的公共初始化：关中断、关 NMI/渲染、关 APU 帧 IRQ 与 DMC IRQ、栈归位。 */
function emitPrologue(a) {
  a.emit(0x78);                         // SEI
  a.emit(0xD8);                         // CLD
  a.ldaImm(0x00); a.staAbs(0x2000);     // 图案表 $0000，NMI 关
  a.ldaImm(0x00); a.staAbs(0x2001);     // 渲染关
  a.ldaImm(0x40); a.staAbs(0x4017);     // APU 帧 IRQ 禁止
  a.ldaImm(0x00); a.staAbs(0x4010);     // DMC IRQ 关
  a.ldxImm(0xFF); a.emit(0x9A);         // TXS
}

/**
 * 比较例程（读 mapper 窗口版）：A = 期望值，X = 结果槽。
 *   STA $03FA / LDA addr / STA $0310,X / CMP $03FA / BEQ ok / INC $03F0 / ok: RTS
 */
function emitCheckerAbs(a, name, addr) {
  a.label(name);
  a.staAbs(0x03FA);
  a.ldaAbs(addr);
  a.staAbsX(0x0310);
  a.cmpAbs(0x03FA);
  a.beq(`${name}_ok`);
  a.incAbs(0x03F0);
  a.label(`${name}_ok`);
  a.rts();
}

/**
 * 比较例程（值已在内存版）：A = 期望值，$03FB = 实际值，X = 结果槽。
 * 走 PPU 读回来的值就用这个 —— 值不是从某个 CPU 地址现取的。
 */
function emitCheckerVal(a, name) {
  a.label(name);
  a.staAbs(0x03FA);
  a.ldaAbs(0x03FB);
  a.staAbsX(0x0310);
  a.cmpAbs(0x03FA);
  a.beq(`${name}_ok`);
  a.incAbs(0x03F0);
  a.label(`${name}_ok`);
  a.rts();
}

/** 往 PPU 的 addr 写 value（走 $2006/$2007）。 */
function emitPpuWrite(a, addr, value) {
  a.ldaImm((addr >> 8) & 0x3F); a.staAbs(0x2006);
  a.ldaImm(addr & 0xFF); a.staAbs(0x2006);
  a.ldaImm(value); a.staAbs(0x2007);
}

/** 读 PPU 的 addr，和期望值比较，结果放 X 槽（第一次读是哑读，喂读缓冲）。 */
function emitPpuCheck(a, addr, expected, slot) {
  a.ldaImm((addr >> 8) & 0x3F); a.staAbs(0x2006);
  a.ldaImm(addr & 0xFF); a.staAbs(0x2006);
  a.ldaAbs(0x2007);                     // 哑读：返回旧缓冲，同时把 addr 的内容装进缓冲
  a.ldaAbs(0x2007);                     // 真读
  a.staAbs(0x03FB);                     // 实际值
  a.ldxImm(slot);
  a.ldaImm(expected);
  a.jsr("checkVal");
}

/** MMC1 的串行写：连写 5 次（低位先），最后一次的地址决定写的是哪个寄存器。 */
function emitMmc1Write(a, base, value) {
  for (let i = 0; i < 5; i++) {
    a.ldaImm((value >> i) & 0x01);
    a.staAbs(base);
  }
}

/** 收尾：测试条数写 $03F1、完成标志写 $03F2，然后原地打转。 */
function emitEpilogue(a, testCount) {
  a.ldaImm(testCount); a.staAbs(0x03F1);
  a.ldaImm(0xA5); a.staAbs(0x03F2);
  a.label("done");
  a.jmp("done");
}

/**
 * 把两个比较例程放到最后。
 * 必须放在 epilogue（那句永远跳自己的 JMP）**之后**：否则主流程执行完最后一个
 * JSR 返回时会一头掉进例程体里，用空栈再 RTS 一次，PC 就飞到 RAM 里去了（踩过一次）。
 */
function emitCheckers(a, absChecks, needsVal = false) {
  a.emit(0xEA);   // 一个 NOP 当分隔，好看
  for (const [name, addr] of absChecks) {
    emitCheckerAbs(a, name, addr);
  }

  if (needsVal) {
    emitCheckerVal(a, "checkVal");
  }
}

// ---------------------------------------------------------------- ROM 组装

const marker16 = (k) => 0x10 + k;
const marker8 = (k) => 0x20 + k;
const marker32 = (k) => 0x30 + k;

/**
 * 铺 PRG：每块 bank 开头放特征字节、其余填 NOP，代码搬到 codeOffset，
 * 最后在 PRG 末尾写向量表。
 *
 * codeBankCpuBase 是"代码所在那块 bank 常驻的 CPU 地址"（$C000 或 $E000），
 * 向量必须指向那边，CPU 才找得到复位入口。
 */
function buildPrg(prgSize, bankSize, markerFn, buildCode, codeOffset, codeBankCpuBase, irqLabel) {
  const prg = Buffer.alloc(prgSize);

  for (let bank = 0; bank < prgSize / bankSize; bank++) {
    const base = bank * bankSize;
    prg.fill(0xEA, base, base + bankSize);
    prg[base] = markerFn(bank) & 0xFF;
  }

  // 代码放在最后一块 bank 里，那块 bank 常驻在 codeBankCpuBase 上
  const codeBase = codeBankCpuBase + (codeOffset - (prgSize - bankSize));
  const a = createAssembler(codeBase);
  buildCode(a);
  a.resolve();

  if (codeOffset + a.code.length > prgSize - 6) {
    throw new Error(`代码放不下：offset ${codeOffset}，长度 ${a.code.length}`);
  }

  Buffer.from(a.code).copy(prg, codeOffset);

  const resetAddr = a.labels["reset"];
  const irqAddr = irqLabel ? a.labels[irqLabel] : resetAddr;
  const vectorOffset = prgSize - 6;
  const writeVector = (offset, value) => {
    prg[offset] = value & 0xFF;
    prg[offset + 1] = (value >> 8) & 0xFF;
  };
  writeVector(vectorOffset + 0, resetAddr);
  writeVector(vectorOffset + 2, resetAddr);
  writeVector(vectorOffset + 4, irqAddr);

  return prg;
}

/**
 * CHR ROM 每 1KB 块的布局：
 *   - tile 0（前 16 字节）：纯色块，颜色号 = ((block & 3) % 3) + 1 —— 画图测试用；
 *   - tile 1（第 16-31 字节）：16 个字节全是"这块 1KB 在 CHR 里的序号"—— **身份标记**。
 *
 * 身份标记放在 tile 1，是为了让它既不干扰"整屏铺 tile 0"的画面对比，
 * 又能被 CPU 侧读到：PPU 的 $0000-$1FFF 就是 CHR，走 $2006/$2007 就能读回来，
 * 于是"CHR 换 bank 到底换对没有"也能用手算值 + 参照实现差分来验，不必画图。
 */
function buildChr(chrSize) {
  const chr = Buffer.alloc(chrSize);
  for (let block = 0; block < chrSize / 0x400; block++) {
    const base = block * 0x400;
    const color = ((block & 3) % 3) + 1;
    const lo = (color & 1) ? 0xFF : 0x00;
    const hi = (color & 2) ? 0xFF : 0x00;
    for (let i = 0; i < 16; i++) {
      chr[base + i] = (i % 16) < 8 ? lo : hi;
    }
    for (let i = 16; i < 32; i++) {
      chr[base + i] = block & 0xFF;
    }
  }
  return chr;
}

/** 读 CHR 里某个 1KB 块的身份标记（tile 1 的第一个字节）并核对。 */
function emitChrIdCheck(a, ppuAddr, expectedBlock, slot) {
  emitPpuCheck(a, ppuAddr + 0x10, expectedBlock & 0xFF, slot);
}

function writeRom(name, header, prg, chr) {
  if (!fs.existsSync(OUT_DIR)) fs.mkdirSync(OUT_DIR, { recursive: true });
  const outPath = path.join(OUT_DIR, name);
  fs.writeFileSync(outPath, Buffer.concat([Buffer.from(header), prg, chr]));
  console.log(`已生成 ${outPath}（PRG ${prg.length / 1024}KB，CHR ${chr.length / 1024}KB）`);
}

/** flags6 的高半字节是 mapper 号低 4 位；bit0 是镜像位。 */
function makeHeader(prg16k, chr8k, mapper, flags6Extra = 0) {
  const h = Buffer.alloc(16);
  h.write("NES\x1a", 0, "binary");
  h[4] = prg16k;
  h[5] = chr8k;
  h[6] = ((mapper & 0x0F) << 4) | (flags6Extra & 0x0F);
  h[7] = mapper & 0xF0;              // 高 4 位（mapper ≥ 16 时需要，比如 23）
  return h;
}

// ---------------------------------------------------------------- mapper 2 / UxROM

function buildMapper2() {
  const PRG_BANKS = 8;                        // 128KB
  const prgSize = PRG_BANKS * 0x4000;
  const codeOffset = prgSize - 0x400;         // 最后一块 bank 的尾部（$FC00）

  const prg = buildPrg(prgSize, 0x4000, marker16, (a) => {
    a.label("reset");
    emitPrologue(a);

    // 复位后 $8000 = bank 0，$C000 = 最后一块
    a.ldxImm(0); a.ldaImm(marker16(0)); a.jsr("check8000");
    a.ldxImm(1); a.ldaImm(marker16(7)); a.jsr("checkC000");

    // 写 $8000 换 bank 3
    a.ldaImm(3); a.staAbs(0x8000);
    a.ldxImm(2); a.ldaImm(marker16(3)); a.jsr("check8000");
    a.ldxImm(3); a.ldaImm(marker16(7)); a.jsr("checkC000");

    // 换到最后一块
    a.ldaImm(7); a.staAbs(0x8000);
    a.ldxImm(4); a.ldaImm(marker16(7)); a.jsr("check8000");

    // 写超出 bank 数的值 → 按 bank 数取模绕回
    a.ldaImm(0xFF); a.staAbs(0x8000);
    a.ldxImm(5); a.ldaImm(marker16(7)); a.jsr("check8000");

    // 换回 bank 0
    a.ldaImm(0); a.staAbs(0x8000);
    a.ldxImm(6); a.ldaImm(marker16(0)); a.jsr("check8000");

    emitEpilogue(a, 7);
    emitCheckers(a, [["check8000", 0x8000], ["checkC000", 0xC000]]);
  }, codeOffset, 0xC000);
  writeRom("mapper2.nes", makeHeader(PRG_BANKS, 1, 2), prg, buildChr(0x2000));
}

// ---------------------------------------------------------------- mapper 1 / MMC1

function buildMapper1() {
  const PRG_BANKS = 8;
  const prgSize = PRG_BANKS * 0x4000;
  const codeOffset = prgSize - 0x400;

  const prg = buildPrg(prgSize, 0x4000, marker16, (a) => {
    a.label("reset");
    emitPrologue(a);

    // MMC1 的"标准开机动作"：往 $8000-$FFFF 写一个 bit7 = 1 的值，清空移位寄存器
    // 并把 PRG 模式强制成 3（最后一块固定在 $C000）。所有真游戏开头都有这一句 ——
    // 不写它就是在依赖"上电时那几个位是什么"这种未定义行为（参照实现就是在这上面翻车的）。
    a.ldaImm(0x80); a.staAbs(0x8000);

    // 复位后：控制寄存器是 $1F → PRG 模式 3 → $8000 可换、$C000 固定最后一块
    a.ldxImm(0); a.ldaImm(marker16(0)); a.jsr("check8000");
    a.ldxImm(1); a.ldaImm(marker16(7)); a.jsr("checkC000");

    // PRG bank 寄存器（$E000 段）= 3
    emitMmc1Write(a, 0xE000, 3);
    a.ldxImm(2); a.ldaImm(marker16(3)); a.jsr("check8000");
    a.ldxImm(3); a.ldaImm(marker16(7)); a.jsr("checkC000");

    // 先把 bank 设成 7：后面切 32KB 模式时，代码所在那块才留在 $C000
    emitMmc1Write(a, 0xE000, 7);

    // 控制 = $02：PRG 模式 0（32KB 块，bank 号低位被忽略）→ banks 6,7
    emitMmc1Write(a, 0x8000, 0x02);
    a.ldxImm(4); a.ldaImm(marker16(6)); a.jsr("check8000");
    a.ldxImm(5); a.ldaImm(marker16(7)); a.jsr("checkC000");

    // 控制 = $0A：PRG 模式 2（$8000 固定第一块，$C000 可换）
    emitMmc1Write(a, 0x8000, 0x0A);
    a.ldxImm(6); a.ldaImm(marker16(0)); a.jsr("check8000");
    a.ldxImm(7); a.ldaImm(marker16(7)); a.jsr("checkC000");

    // 控制 = $0C：模式 3（$8000 可换，$C000 固定最后一块）
    emitMmc1Write(a, 0x8000, 0x0C);
    a.ldxImm(8); a.ldaImm(marker16(7)); a.jsr("check8000");
    a.ldxImm(9); a.ldaImm(marker16(7)); a.jsr("checkC000");

    // 镜像 = 水平（控制低 2 位 = 3）：$2000 和 $2400 是同一块，$2800 在另一块
    emitMmc1Write(a, 0x8000, 0x0F);
    emitPpuWrite(a, 0x2000, 0x11);
    emitPpuCheck(a, 0x2400, 0x11, 10);
    emitPpuCheck(a, 0x2800, 0x00, 11);

    // 镜像 = 垂直（= 2）：$2000 和 $2800 同一块，$2400 换到没写过的那块
    emitMmc1Write(a, 0x8000, 0x0E);
    emitPpuCheck(a, 0x2800, 0x11, 12);
    emitPpuCheck(a, 0x2400, 0x00, 13);

    // 镜像 = 单屏低（= 0）：四块名称表全指向第一块
    emitMmc1Write(a, 0x8000, 0x0C);
    emitPpuCheck(a, 0x2400, 0x11, 14);
    emitPpuCheck(a, 0x2C00, 0x11, 15);

    // CHR：模式 0（8KB）时 chrBank0 = 2 → $0000 用 4KB bank 2（1KB 块 8-11）、
    // $1000 用 4KB bank 3（块 12-15）。块号读 tile 1 的第一个字节就能认出来。
    emitMmc1Write(a, 0xA000, 2);
    emitChrIdCheck(a, 0x0000, 8, 16);
    emitChrIdCheck(a, 0x1000, 12, 17);

    // CHR：模式 1（两块 4KB 各换各的）→ 控制 bit4 = 1，chrBank0 = 2、chrBank1 = 5
    emitMmc1Write(a, 0x8000, 0x1C);
    emitMmc1Write(a, 0xC000, 5);
    emitChrIdCheck(a, 0x0000, 8, 18);
    emitChrIdCheck(a, 0x1000, 20, 19);

    emitEpilogue(a, 20);
    emitCheckers(a, [["check8000", 0x8000], ["checkC000", 0xC000]], true);
  }, codeOffset, 0xC000);

  writeRom("mapper1.nes", makeHeader(PRG_BANKS, 4, 1), prg, buildChr(0x8000));
}

// ---------------------------------------------------------------- mapper 3 / CNROM

function buildMapper3() {
  const PRG_BANKS = 2;                        // 32KB，固定不换
  const prgSize = PRG_BANKS * 0x4000;
  const codeOffset = prgSize - 0x400;

  const prg = buildPrg(prgSize, 0x4000, marker16, (a) => {
    a.label("reset");
    emitPrologue(a);

    a.ldxImm(0); a.ldaImm(marker16(0)); a.jsr("check8000");
    a.ldxImm(1); a.ldaImm(marker16(1)); a.jsr("checkC000");

    // 写 $8000-$FFFF 只换 CHR，PRG 一点不动 —— 这就是 CNROM 的全部。
    // 顺便核对 CHR 真的换过去了：8KB bank b 的第一个 1KB 块是全局第 b*8 块。
    for (let n = 1; n <= 3; n++) {
      a.ldaImm(n); a.staAbs(0x8000);
      a.ldxImm(n * 2); a.ldaImm(marker16(0)); a.jsr("check8000");
      a.ldxImm((n * 2) + 1); a.ldaImm(marker16(1)); a.jsr("checkC000");
      emitChrIdCheck(a, 0x0000, n * 8, 16 + n);
    }

    emitEpilogue(a, 12);
    emitCheckers(a, [["check8000", 0x8000], ["checkC000", 0xC000]], true);
  }, codeOffset, 0xC000);

  writeRom("mapper3.nes", makeHeader(PRG_BANKS, 4, 3), prg, buildChr(0x8000));
}

// ---------------------------------------------------------------- mapper 23 / VRC2b
//
// 寄存器布局（VRC2b/VRC4e）：$8000/$A000 换 8KB PRG，$9000 镜像（bit0：0=垂直 1=水平），
// $B000/$B001 写 CHR0 的低/高半字节、$B002/$B003 写 CHR1 的，依此类推；$C000/$E000 固定。
function buildMapper23() {
  const PRG_BANKS = 16;                       // 128KB，8KB 一块
  const prgSize = PRG_BANKS * 0x2000;
  const codeOffset = prgSize - 0x600;

  const prg = buildPrg(prgSize, 0x2000, marker8, (a) => {
    a.label("reset");
    emitPrologue(a);

    // 复位后：$8000=0、$A000=1、$C000=倒数第二块(14)、$E000=最后一块(15)
    a.ldxImm(0); a.ldaImm(marker8(0)); a.jsr("check8000");
    a.ldxImm(1); a.ldaImm(marker8(1)); a.jsr("checkA000");
    a.ldxImm(2); a.ldaImm(marker8(14)); a.jsr("checkC000");
    a.ldxImm(3); a.ldaImm(marker8(15)); a.jsr("checkE000");

    // $8000 = 5
    a.ldaImm(5); a.staAbs(0x8000);
    a.ldxImm(4); a.ldaImm(marker8(5)); a.jsr("check8000");

    // $A000 = 9
    a.ldaImm(9); a.staAbs(0xA000);
    a.ldxImm(5); a.ldaImm(marker8(9)); a.jsr("checkA000");

    // 镜像：$9000 bit0 = 1 → 水平（表0/表1 同一块）
    a.ldaImm(1); a.staAbs(0x9000);
    emitPpuWrite(a, 0x2000, 0x5A);
    emitPpuCheck(a, 0x2400, 0x5A, 6);
    emitPpuCheck(a, 0x2800, 0x00, 7);

    // 镜像：bit0 = 0 → 垂直（表0/表2 同一块）
    a.ldaImm(0); a.staAbs(0x9000);
    emitPpuCheck(a, 0x2400, 0x00, 8);
    emitPpuCheck(a, 0x2800, 0x5A, 9);

    // CHR：CHR0 = $03（低半字节写 $B000、高半字节写 $B001），CHR1 = $12（$B002/$B003）
    a.ldaImm(3);    a.staAbs(0xB000);
    a.ldaImm(0);    a.staAbs(0xB001);
    a.ldaImm(0x12); a.staAbs(0xB002);
    a.ldaImm(1);    a.staAbs(0xB003);
    emitChrIdCheck(a, 0x0000, 3, 10);
    emitChrIdCheck(a, 0x0400, 0x12, 11);

    // CHR2 = $25（$C000/$C001），CHR7 = $40（$E002/$E003）
    a.ldaImm(5);    a.staAbs(0xC000);      // CHR2 低半字节
    a.ldaImm(2);    a.staAbs(0xC001);      // CHR2 高半字节 → $25
    a.ldaImm(0);    a.staAbs(0xE002);      // CHR7 低半字节
    a.ldaImm(4);    a.staAbs(0xE003);      // CHR7 高半字节 → $40
    emitChrIdCheck(a, 0x0800, 0x25, 12);
    emitChrIdCheck(a, 0x1C00, 0x40, 13);

    emitEpilogue(a, 14);
    emitCheckers(a, [
      ["check8000", 0x8000], ["checkA000", 0xA000],
      ["checkC000", 0xC000], ["checkE000", 0xE000],
    ], true);
  }, codeOffset, 0xE000);

  writeRom("mapper23.nes", makeHeader(PRG_BANKS / 2, 0x10, 23), prg, buildChr(0x20000));
}
// ---------------------------------------------------------------- mapper 4 / MMC3

function buildMapper4() {
  const PRG_BANKS = 16;                       // 128KB，8KB 一块
  const prgSize = PRG_BANKS * 0x2000;
  const codeOffset = prgSize - 0x600;         // bank 15 的尾部（$FA00）

  const prg = buildPrg(prgSize, 0x2000, marker8, (a) => {
    a.label("reset");
    emitPrologue(a);

    // 复位后 R6=R7=0：$8000=bank0、$A000=bank1、$C000=倒数第二块、$E000=最后一块
    a.ldxImm(0); a.ldaImm(marker8(0)); a.jsr("check8000");
    a.ldxImm(1); a.ldaImm(marker8(1)); a.jsr("checkA000");
    a.ldxImm(2); a.ldaImm(marker8(14)); a.jsr("checkC000");
    a.ldxImm(3); a.ldaImm(marker8(15)); a.jsr("checkE000");

    // R6 = 5
    a.ldaImm(6); a.staAbs(0x8000);
    a.ldaImm(5); a.staAbs(0x8001);
    a.ldxImm(4); a.ldaImm(marker8(5)); a.jsr("check8000");
    a.ldxImm(5); a.ldaImm(marker8(14)); a.jsr("checkC000");

    // R7 = 9
    a.ldaImm(7); a.staAbs(0x8000);
    a.ldaImm(9); a.staAbs(0x8001);
    a.ldxImm(6); a.ldaImm(marker8(9)); a.jsr("checkA000");

    // PRG 模式 1：$8000 = 倒数第二块，$C000 = R6
    a.ldaImm(0x40); a.staAbs(0x8000);
    a.ldxImm(7); a.ldaImm(marker8(14)); a.jsr("check8000");
    a.ldxImm(8); a.ldaImm(marker8(5)); a.jsr("checkC000");
    a.ldxImm(9); a.ldaImm(marker8(15)); a.jsr("checkE000");

    // 镜像：$A000 = 0 → 垂直（表0/表2 同一块）；= 1 → 水平（表0/表1 同一块）
    a.ldaImm(0); a.staAbs(0xA000);
    emitPpuWrite(a, 0x2000, 0x33);
    emitPpuCheck(a, 0x2400, 0x00, 10);
    emitPpuCheck(a, 0x2800, 0x33, 11);

    a.ldaImm(1); a.staAbs(0xA000);
    emitPpuCheck(a, 0x2400, 0x33, 12);
    emitPpuCheck(a, 0x2800, 0x00, 13);

    // CHR 模式 0：R0 = 2 → $0000/$0400 是 1KB 块 2、3；R1 = 4 → $0800/$0C00 是块 4、5；
    // R2 = 8 → $1000 是块 8
    a.ldaImm(0); a.staAbs(0x8000);            // 选 R0（同时把模式位清 0）
    a.ldaImm(2); a.staAbs(0x8001);
    a.ldaImm(1); a.staAbs(0x8000);
    a.ldaImm(4); a.staAbs(0x8001);
    a.ldaImm(2); a.staAbs(0x8000);
    a.ldaImm(8); a.staAbs(0x8001);
    emitChrIdCheck(a, 0x0000, 2, 14);
    emitChrIdCheck(a, 0x0400, 3, 15);
    emitChrIdCheck(a, 0x0800, 4, 16);
    emitChrIdCheck(a, 0x1000, 8, 17);

    // CHR 模式 1（A12 反转，$8000 的 bit7 = 1）：R2-R5 换到 $0000，R0/R1 的 2KB 换到 $1000
    a.ldaImm(0x80); a.staAbs(0x8000);
    emitChrIdCheck(a, 0x0000, 8, 18);
    emitChrIdCheck(a, 0x1000, 2, 19);
    emitChrIdCheck(a, 0x1400, 3, 20);

    emitEpilogue(a, 21);
    emitCheckers(a, [
      ["check8000", 0x8000], ["checkA000", 0xA000],
      ["checkC000", 0xC000], ["checkE000", 0xE000],
    ], true);
  }, codeOffset, 0xE000);

  writeRom("mapper4.nes", makeHeader(PRG_BANKS / 2, 4, 4), prg, buildChr(0x8000));
}

// ---------------------------------------------------------------- mapper 7 / AxROM

function buildMapper7() {
  const PRG_BANKS = 4;                        // 4 × 32KB = 128KB
  const BANK_SIZE = 0x8000;
  const prgSize = PRG_BANKS * BANK_SIZE;

  // 代码要**复制到每一块 32KB bank 的同一个位置**：AxROM 换 bank 时整个 $8000-$FFFF
  // 都会换掉，正在执行的代码必须在新 bank 里也在那儿（真机的 AOROM 游戏就是这么排的）。
  const codeOffsetInBank = 0x100;

  const a = createAssembler(0x8000 + codeOffsetInBank);
  a.label("reset");
  emitPrologue(a);

  a.ldxImm(0); a.ldaImm(marker32(0)); a.jsr("check8000");

  a.ldaImm(2); a.staAbs(0x8000);
  a.ldxImm(1); a.ldaImm(marker32(2)); a.jsr("check8000");

  a.ldaImm(1); a.staAbs(0x8000);
  a.ldxImm(2); a.ldaImm(marker32(1)); a.jsr("check8000");

  // bit4 = 1 → bank 3 + 单屏高
  a.ldaImm(0x13); a.staAbs(0x8000);
  a.ldxImm(3); a.ldaImm(marker32(3)); a.jsr("check8000");

  // 单屏高时往 $2000 写 $44 → 落到第二块 1KB
  emitPpuWrite(a, 0x2000, 0x44);

  // 切回 bank 0 + 单屏低（bit4 = 0）→ $2000 换到第一块 1KB，没写过，应该读到 0
  a.ldaImm(0x00); a.staAbs(0x8000);
  emitPpuCheck(a, 0x2000, 0x00, 4);

  // 再切回单屏高 → $44 还在
  a.ldaImm(0x13); a.staAbs(0x8000);
  emitPpuCheck(a, 0x2000, 0x44, 5);

  emitEpilogue(a, 6);
  emitCheckers(a, [["check8000", 0x8000]], true);
  a.resolve();

  const prg = Buffer.alloc(prgSize);
  for (let bank = 0; bank < PRG_BANKS; bank++) {
    const base = bank * BANK_SIZE;
    prg.fill(0xEA, base, base + BANK_SIZE);
    prg[base] = marker32(bank) & 0xFF;
    Buffer.from(a.code).copy(prg, base + codeOffsetInBank);

    // 向量表也要**每块 bank 都写**：AxROM 上电时用哪一块 bank 是不确定的，
    // 只在最后一块写向量的话，上电读到的是别的 bank 的 NOP，CPU 直接从 $EAEA 开始跑（踩过一次）。
    const resetAddr = a.labels["reset"];
    const vectorOffset = base + BANK_SIZE - 6;
    const writeVector = (offset, value) => {
      prg[offset] = value & 0xFF;
      prg[offset + 1] = (value >> 8) & 0xFF;
    };
    writeVector(vectorOffset + 0, resetAddr);
    writeVector(vectorOffset + 2, resetAddr);
    writeVector(vectorOffset + 4, resetAddr);
  }

  // CHR 是卡带上的 8KB RAM（AOROM 板没有 CHR ROM），所以头里 CHR bank 数写 0
  writeRom("mapper7.nes", makeHeader(PRG_BANKS * 2, 0, 7), prg, Buffer.alloc(0));
}

// ---------------------------------------------------------------- mapper 4 的扫描线 IRQ

/** 每次中断重新装载的扫描线条数。 */
const IRQ_LATCH = 100;

/**
 * MMC3 的扫描线中断：这是分屏（状态栏固定、画面下半部分滚动）的唯一手段，
 * 也是 MMC3 和前几块 mapper 最大的区别。
 *
 * 计数器每渲染一条可见扫描线减 1，减到 0 就拉 IRQ；中断处理程序里重新装载 + 重新使能，
 * 于是中断会以 **IRQ_LATCH + 1 条扫描线**为周期反复触发。
 *
 * 这台机器一帧有 240 条可见扫描线，所以中断次数 ≈ 帧数 × 240 / 101 —— 跑 12 帧应该在
 * 28 次上下。这个 ROM 不做 trace 差分（参照实现的 PPU 是 dot 级、中断时刻本来就不一样），
 * 只用"次数落在理论区间内"来判定。
 */
function buildMapper4Irq() {
  const PRG_BANKS = 16;
  const prgSize = PRG_BANKS * 0x2000;
  const codeOffset = prgSize - 0x600;

  const prg = buildPrg(prgSize, 0x2000, marker8, (a) => {
    a.label("reset");
    emitPrologue(a);                       // 里面已经关掉 NMI / APU 帧 IRQ / DMC IRQ

    a.ldaImm(0x00); a.staAbs(0x0300);      // IRQ 计数
    a.ldaImm(0x00); a.staAbs(0x0301);      // 处理程序"正在跑"标记（跑飞了能看出来）
    a.ldaImm(IRQ_LATCH); a.staAbs(0xC000); // IRQ latch
    a.ldaImm(0x00); a.staAbs(0xC001);      // 请求重装
    a.ldaImm(0x00); a.staAbs(0xE000);      // 先关中断
    a.ldaAbs(0x2002);                      // 清 PPU 状态（读 $2002）
    a.emit(0x58);                          // CLI
    a.ldaImm(0x0A); a.staAbs(0x2001);      // 开背景渲染 → 计数器开始按扫描线走
    a.ldaImm(0x00); a.staAbs(0xE001);      // 允许 IRQ

    a.label("spin");
    a.jmp("spin");

    // IRQ 处理程序：计数 +1，然后重新武装（必须先关再重装，不然中断线会一直拉着）
    a.label("irq");
    a.emit(0x48);                          // PHA
    a.ldaImm(0x01); a.staAbs(0x0301);      // 标记"进过中断"
    a.incAbs(0x0300);
    a.ldaImm(0x00); a.staAbs(0xE000);      // 关中断（放开 IRQ 线）
    a.ldaImm(IRQ_LATCH); a.staAbs(0xC000);
    a.ldaImm(0x00); a.staAbs(0xC001);
    a.ldaImm(0x00); a.staAbs(0xE001);      // 再允许
    a.ldaImm(0x00); a.staAbs(0x0301);
    a.emit(0x68);                          // PLA
    a.emit(0x40);                          // RTI
  }, codeOffset, 0xE000, "irq");

  // 顺便把 MMC3 用 CHR RAM 的那条路也走一遍：头里 CHR bank 数 = 0
  writeRom("mapper4-irq.nes", makeHeader(PRG_BANKS / 2, 0, 4), prg, Buffer.alloc(0));
}

buildMapper1();
buildMapper2();
buildMapper3();
buildMapper23();
buildMapper4();
buildMapper7();
buildMapper4Irq();
