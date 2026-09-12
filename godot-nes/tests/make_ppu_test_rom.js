// 生成 PPU 测试 ROM：真正的 6502 程序，把 PPU 配置成一幅**每个像素都能手算出来**的画面。
//
//   node tests/make_ppu_test_rom.js
//   → tests/roms/ppu-test.nes    滚动 (0, 0)
//   → tests/roms/ppu-scroll.nes  滚动 (3, 5)，用来验证滚动和名称表切换
//
// 画面设计（故意做成容易核对的样子）：
//   - 整个名称表铺 tile 0；tile 0 的图案是「左边 4 列颜色号 1，右边 4 列颜色号 2」；
//   - 属性表全部填 $E4，于是 4 个子区域依次用调色板 0/1/2/3 →
//     屏幕上出现 16x16 像素一块的四种调色板棋盘格；
//   - 两个精灵（tile 1，同样的左右分色），一个正常、一个水平翻转 + 另一套精灵调色板；
//   - OAM 走 $4014 DMA 上传（顺便测 DMA）；
//   - 开 NMI，中断处理程序把 $0300 自增（用来确认 NMI 真的在跑）。
//
// 滚动版会把两块名称表都填上**不同的内容**（左上 $20 填 tile 0，右上 $24 填 tile 2），
// 这样横向滚动跨过 256 像素时能看出名称表切换对不对。

const fs = require("fs");
const path = require("path");

const OUT_DIR = path.join(__dirname, "roms");
const BASE = 0x8000;
const PRG_SIZE = 0x4000; // 16KB
const CHR_SIZE = 0x2000; // 8KB

// ---------------------------------------------------------------- 迷你汇编器

function createAssembler() {
  const code = [];
  const labels = {};
  const fixups = [];

  // 故意写成闭包而不是对象方法：调用方会解构出来用，那样 this 就丢了
  const addr = () => BASE + code.length;

  function emit(...bytes) {
    for (const b of bytes) code.push(b & 0xFF);
  }

  function label(name) {
    labels[name] = addr();
  }

  // 16 位绝对地址（操作数低字节在前）；pos 指的是**操作数**位置，别写到操作码上
  function abs(opcode, target) {
    const pos = code.length + 1;
    emit(opcode, 0, 0);
    fixups.push({ pos, target });
  }

  function branch(opcode, target) {
    const pos = code.length + 1;
    emit(opcode, 0);
    fixups.push({ pos, target, relative: true, base: addr() });
  }

  function resolve() {
    for (const f of fixups) {
      const target = labels[f.target];
      if (target === undefined) throw new Error(`未定义的标签 ${f.target}`);
      if (f.relative) {
        const offset = target - f.base;
        if (offset < -128 || offset > 127) throw new Error(`分支 ${f.target} 偏移越界`);
        code[f.pos] = offset & 0xFF;
      } else {
        code[f.pos] = target & 0xFF;
        code[f.pos + 1] = (target >> 8) & 0xFF;
      }
    }
    fixups.length = 0;
  }

  return { code, labels, addr, emit, label, abs, branch, resolve };
}

// ---------------------------------------------------------------- 生成一个 ROM

function buildRom(name, scrollX, scrollY) {
  const asm = createAssembler();
  const { emit, label, abs: emitAbs, branch: emitBranch } = asm;

  label("reset");
  emit(0x78); // SEI
  emit(0xD8); // CLD
  emit(0xA2, 0x40, 0x8E, 0x17, 0x40); // LDX #$40 / STX $4017  关掉 APU 帧中断
  emit(0xA2, 0xFF, 0x9A); // LDX #$FF / TXS
  emit(0xE8); // INX → X = 0
  emit(0x8E, 0x00, 0x20); // STX $2000    NMI 关、图案表 $0000、名称表 $2000
  emit(0x8E, 0x01, 0x20); // STX $2001    渲染关
  emit(0x8E, 0x10, 0x40); // STX $4010    关 DMC IRQ

  // 等两次 VBlank，让 PPU 稳定下来
  label("wait1");
  emit(0x2C, 0x02, 0x20); // BIT $2002
  emitBranch(0x10, "wait1"); // BPL wait1
  label("wait2");
  emit(0x2C, 0x02, 0x20); // BIT $2002
  emitBranch(0x10, "wait2"); // BPL wait2

  // 清空 RAM $0000-$07FF
  emit(0xA9, 0x00); // LDA #$00
  emit(0xA2, 0x00); // LDX #$00
  label("clear_ram");
  emit(0x95, 0x00); // STA $00,X
  emit(0x9D, 0x00, 0x01);
  emit(0x9D, 0x00, 0x02);
  emit(0x9D, 0x00, 0x03);
  emit(0x9D, 0x00, 0x04);
  emit(0x9D, 0x00, 0x05);
  emit(0x9D, 0x00, 0x06);
  emit(0x9D, 0x00, 0x07);
  emit(0xE8); // INX
  emitBranch(0xD0, "clear_ram");

  // 上传调色板：$3F00 起 32 字节
  emit(0xA9, 0x3F, 0x8D, 0x06, 0x20); // LDA #$3F / STA $2006
  emit(0xA9, 0x00, 0x8D, 0x06, 0x20); // LDA #$00 / STA $2006
  emit(0xA2, 0x00); // LDX #$00
  label("pal_loop");
  emitAbs(0xBD, "palette"); // LDA palette,X
  emit(0x8D, 0x07, 0x20); // STA $2007
  emit(0xE8); // INX
  emit(0xE0, 0x20); // CPX #$20
  emitBranch(0xD0, "pal_loop");

  // 名称表：$2000 填 tile 0；滚动版再填 $2400 为 tile 2
  emit(0xA9, 0x20, 0x8D, 0x06, 0x20); // LDA #$20 / STA $2006
  emit(0xA9, 0x00, 0x8D, 0x06, 0x20); // LDA #$00 / STA $2006
  emit(0xA9, 0x00); // LDA #$00  (tile 0)
  emit(0xA2, 0x00); // LDX #$00
  emit(0xA0, 0x04); // LDY #$04
  label("nt_outer");
  label("nt_inner");
  emit(0x8D, 0x07, 0x20); // STA $2007
  emit(0xE8); // INX
  emit(0xE0, 0xF0); // CPX #$F0
  emitBranch(0xD0, "nt_inner");
  emit(0xA2, 0x00); // LDX #$00
  emit(0x88); // DEY
  emitBranch(0xD0, "nt_outer"); // 4 × 240 = 960 字节

  if (scrollX !== 0) {
    // 第二块名称表 $2400 填 tile 2 —— 横向滚过 256 像素后应该看到它
    emit(0xA9, 0x24, 0x8D, 0x06, 0x20); // LDA #$24 / STA $2006
    emit(0xA9, 0x00, 0x8D, 0x06, 0x20); // LDA #$00 / STA $2006
    emit(0xA9, 0x02); // LDA #$02  (tile 2)
    emit(0xA2, 0x00); // LDX #$00
    emit(0xA0, 0x04); // LDY #$04
    label("nt2_outer");
    label("nt2_inner");
    emit(0x8D, 0x07, 0x20);
    emit(0xE8);
    emit(0xE0, 0xF0);
    emitBranch(0xD0, "nt2_inner");
    emit(0xA2, 0x00);
    emit(0x88);
    emitBranch(0xD0, "nt2_outer");
  }

  // 属性表：$23C0 起 64 字节全填 $E4（四个子区域依次用调色板 0/1/2/3）
  emit(0xA9, 0x23, 0x8D, 0x06, 0x20); // LDA #$23 / STA $2006
  emit(0xA9, 0xC0, 0x8D, 0x06, 0x20); // LDA #$C0 / STA $2006
  emit(0xA9, 0xE4); // LDA #$E4
  emit(0xA2, 0x40); // LDX #$40
  label("attr_loop");
  emit(0x8D, 0x07, 0x20); // STA $2007
  emit(0xCA); // DEX
  emitBranch(0xD0, "attr_loop");

  // OAM 数据写进 RAM $0200，然后走 $4014 DMA
  emit(0xA9, 0x1F, 0x8D, 0x00, 0x02); // 精灵 0：(32, 32)，tile 1，调色板 0
  emit(0xA9, 0x01, 0x8D, 0x01, 0x02);
  emit(0xA9, 0x00, 0x8D, 0x02, 0x02);
  emit(0xA9, 0x20, 0x8D, 0x03, 0x02);
  emit(0xA9, 0x3F, 0x8D, 0x04, 0x02); // 精灵 1：(64, 64)，tile 1，调色板 1 + 水平翻转
  emit(0xA9, 0x01, 0x8D, 0x05, 0x02);
  emit(0xA9, 0x41, 0x8D, 0x06, 0x02);
  emit(0xA9, 0x40, 0x8D, 0x07, 0x02);

  emit(0xA9, 0x00, 0x8D, 0x03, 0x20); // LDA #$00 / STA $2003  OAMADDR = 0
  emit(0xA9, 0x02, 0x8D, 0x14, 0x40); // LDA #$02 / STA $4014  OAM DMA

  // 设置滚动：先写 $2000 定名称表，再写两次 $2005
  emit(0xA9, 0x80, 0x8D, 0x00, 0x20); // LDA #$80 / STA $2000  NMI 开、名称表 $2000
  emit(0xA9, scrollX, 0x8D, 0x05, 0x20); // LDA #scrollX / STA $2005
  emit(0xA9, scrollY, 0x8D, 0x05, 0x20); // LDA #scrollY / STA $2005

  // 开渲染
  emit(0xA9, 0x1E, 0x8D, 0x01, 0x20); // LDA #$1E / STA $2001  开背景和精灵，最左 8 像素也显示

  label("main_loop");
  emitAbs(0x4C, "main_loop"); // JMP main_loop

  // NMI：把 $0300 自增
  label("nmi_handler");
  emit(0x48); // PHA
  emit(0xEE, 0x00, 0x03); // INC $0300
  emit(0x68); // PLA
  emit(0x40); // RTI

  label("irq_handler");
  emit(0x40); // RTI

  label("palette");
  const PALETTE = [
    0x0F, 0x01, 0x02, 0x03, // 背景调色板 0
    0x0F, 0x11, 0x12, 0x13, // 1
    0x0F, 0x21, 0x22, 0x23, // 2
    0x0F, 0x31, 0x32, 0x33, // 3
    0x0F, 0x0A, 0x0B, 0x0C, // 精灵调色板 0
    0x0F, 0x1A, 0x1B, 0x1C, // 1
    0x0F, 0x2A, 0x2B, 0x2C, // 2
    0x0F, 0x3A, 0x3B, 0x3C, // 3
  ];
  for (const color of PALETTE) emit(color);

  // 回填所有 fixup（包括上面那条 LDA palette,X 的地址）
  asm.resolve();

  // 填 NOP 到向量表
  while (asm.code.length < PRG_SIZE - 6) emit(0xEA);
  emit(asm.labels["nmi_handler"] & 0xFF, (asm.labels["nmi_handler"] >> 8) & 0xFF);
  emit(asm.labels["reset"] & 0xFF, (asm.labels["reset"] >> 8) & 0xFF);
  emit(asm.labels["irq_handler"] & 0xFF, (asm.labels["irq_handler"] >> 8) & 0xFF);

  if (asm.code.length !== PRG_SIZE) {
    throw new Error(`PRG 应为 ${PRG_SIZE} 字节，实际 ${asm.code.length}`);
  }

  // CHR
  const chr = buildChr();

  const header = Buffer.alloc(16);
  header.write("NES\x1a", 0, "binary");
  header[4] = 1; // 16KB PRG
  header[5] = 1; // 8KB CHR
  // 滚动版必须用**垂直镜像**，$2000 和 $2400 才是两块独立的名称表（左右并排）。
  // 水平镜像下 $2400 会落到同一块物理表上，那样"名称表切换"根本测不到 —— 踩过一次。
  header[6] = scrollX !== 0 ? 0x01 : 0x00;
  header[7] = 0x00;

  if (!fs.existsSync(OUT_DIR)) fs.mkdirSync(OUT_DIR, { recursive: true });
  const outPath = path.join(OUT_DIR, name);
  fs.writeFileSync(outPath, Buffer.concat([header, Buffer.from(asm.code), chr]));
  console.log(`已生成 ${outPath}（滚动 ${scrollX},${scrollY}）`);
}

/** 把字节数组写成 16 字节头 + PRG + CHR 的 .nes 文件 */

function buildChr() {
  const chr = Buffer.alloc(CHR_SIZE);

  // tile 0 / tile 1：左 4 列颜色号 1、右 4 列颜色号 2
  // 颜色号 = (平面1 << 1) | 平面0
  for (const tile of [0, 1]) {
    for (let row = 0; row < 8; row++) {
      chr[tile * 16 + row] = 0xF0; // 平面 0
      chr[tile * 16 + row + 8] = 0x0F; // 平面 1
    }
  }

  // tile 2：整块颜色号 3（第二块名称表用，和 tile 0 一眼能区分）
  for (let row = 0; row < 8; row++) {
    chr[32 + row] = 0xFF;
    chr[32 + row + 8] = 0xFF;
  }

  // 其余图块给条纹图案，方便看 pattern table 预览
  for (let tile = 3; tile < CHR_SIZE / 16; tile++) {
    for (let row = 0; row < 8; row++) {
      const base = tile * 16 + row;
      chr[base] = (tile * 8 + row) & 0xFF;
      chr[base + 8] = ((tile >> 2) * 8 + row) & 0xFF;
    }
  }

  return chr;
}

buildRom("ppu-test.nes", 0, 0);
buildRom("ppu-scroll.nes", 3, 5);
buildSprite0Rom();

/**
 * 精灵 0 命中的回归 ROM。
 *
 * 布置：名称表整屏铺 tile 0（图案是纯色号 1，也就是**到处都不透明**），
 * 精灵 0 摆在 (100, 100) 并且属性位 bit5 = 1（"在背景后面"）。
 *
 * 真机上"精灵 0 命中"和优先级**无关**：只要精灵 0 的不透明像素压在背景的不透明像素上就置位。
 * 这里就是踩过的坑 —— 先判优先级再判命中的话，这个标志永远不置位，
 * 而 SMB 的标题画面正好死等这个标志（`LDA $2002 / AND #$40 / BEQ`），
 * 结果就是游戏卡死、精灵不出现、按开始也没反应。
 *
 * ROM 自己轮询 $2002 的 bit6（约 1.4 帧），命中就写 $0300 = $A5，超时写 $00。
 */
function buildSprite0Rom() {
  const asm = createAssembler();
  const { emit, label, branch: emitBranch } = asm;

  label("reset");
  emit(0x78); // SEI
  emit(0xD8); // CLD
  emit(0xA2, 0xFF, 0x9A); // LDX #$FF / TXS
  emit(0xA9, 0x00, 0x8D, 0x00, 0x20); // LDA #$00 / STA $2000  关 NMI
  emit(0x8D, 0x01, 0x20); // STA $2001  关渲染

  // 调色板：$3F00 = $0F，$3F01 = $21
  emit(0xA9, 0x3F, 0x8D, 0x06, 0x20);
  emit(0xA9, 0x00, 0x8D, 0x06, 0x20);
  emit(0xA9, 0x0F, 0x8D, 0x07, 0x20);
  emit(0xA9, 0x21, 0x8D, 0x07, 0x20);

  // 名称表 $2000 起写 4 × 256 = 1024 字节的 0（全是 tile 0）
  emit(0xA9, 0x20, 0x8D, 0x06, 0x20);
  emit(0xA9, 0x00, 0x8D, 0x06, 0x20);
  emit(0xA9, 0x00); // LDA #$00
  emit(0xA2, 0x04); // LDX #$04
  label("fill");
  emit(0xA0, 0x00); // LDY #$00
  label("fill_inner");
  emit(0x8D, 0x07, 0x20); // STA $2007
  emit(0xC8); // INY
  emitBranch(0xD0, "fill_inner");
  emit(0xCA); // DEX
  emitBranch(0xD0, "fill");

  // 属性表也清 0（64 字节），保证整屏都用背景调色板 0
  emit(0xA9, 0x23, 0x8D, 0x06, 0x20);
  emit(0xA9, 0xC0, 0x8D, 0x06, 0x20);
  emit(0xA9, 0x00);
  emit(0xA0, 0x40); // LDY #$40
  label("attr_loop");
  emit(0x8D, 0x07, 0x20);
  emit(0x88); // DEY
  emitBranch(0xD0, "attr_loop");

  // 精灵 0：OAM[$00] = Y-1 = 99、tile = 1、attr = $20（在背景后面）、X = 100
  emit(0xA9, 0x00, 0x8D, 0x03, 0x20); // OAMADDR = 0
  emit(0xA9, 99, 0x8D, 0x04, 0x20);
  emit(0xA9, 0x01, 0x8D, 0x04, 0x20);
  emit(0xA9, 0x20, 0x8D, 0x04, 0x20); // bit5 = 1：画在背景后面
  emit(0xA9, 100, 0x8D, 0x04, 0x20);

  // 开背景 + 精灵（左 8 像素也显示）
  emit(0xA9, 0x1E, 0x8D, 0x01, 0x20);

  // 轮询精灵 0 命中：16 × 256 次，大约 1.4 帧
  emit(0xA2, 0x10); // LDX #$10
  label("poll_outer");
  emit(0xA0, 0x00); // LDY #$00
  label("poll_inner");
  emit(0xAD, 0x02, 0x20); // LDA $2002
  emit(0x29, 0x40); // AND #$40
  emitBranch(0xD0, "hit");
  emit(0x88); // DEY
  emitBranch(0xD0, "poll_inner");
  emit(0xCA); // DEX
  emitBranch(0xD0, "poll_outer");

  emit(0xA9, 0x00, 0x8D, 0x00, 0x03); // 超时：$0300 = 0
  emitBranch(0x4C, "done");

  label("hit");
  emit(0xA9, 0xA5, 0x8D, 0x00, 0x03); // $0300 = $A5

  label("done");
  emitBranch(0x4C, "done");

  asm.resolve();

  // tile 0 / tile 1 都是纯色号 1（平面 0 全 1、平面 1 全 0）→ 不透明
  const chr = Buffer.alloc(CHR_SIZE);
  for (const tile of [0, 1]) {
    for (let row = 0; row < 8; row++) {
      chr[tile * 16 + row] = 0xFF;
      chr[tile * 16 + row + 8] = 0x00;
    }
  }

  const prg = Buffer.alloc(PRG_SIZE);
  for (let i = 0; i < PRG_SIZE; i++) prg[i] = 0xEA;
  Buffer.from(asm.code).copy(prg, 0);
  const resetAddr = asm.labels["reset"];
  prg[PRG_SIZE - 6] = resetAddr & 0xFF;
  prg[PRG_SIZE - 5] = (resetAddr >> 8) & 0xFF;
  prg[PRG_SIZE - 4] = resetAddr & 0xFF;
  prg[PRG_SIZE - 3] = (resetAddr >> 8) & 0xFF;
  prg[PRG_SIZE - 2] = resetAddr & 0xFF;
  prg[PRG_SIZE - 1] = (resetAddr >> 8) & 0xFF;

  const header = Buffer.alloc(16);
  header.write("NES\x1a", 0, "binary");
  header[4] = 1; // 16KB PRG
  header[5] = 1; // 8KB CHR
  header[6] = 0x00; // 水平镜像、mapper 0

  const outPath = path.join(OUT_DIR, "ppu-sprite0.nes");
  fs.writeFileSync(outPath, Buffer.concat([header, prg, chr]));
  console.log(`已生成 ${outPath}（精灵 0 命中回归）`);
}
