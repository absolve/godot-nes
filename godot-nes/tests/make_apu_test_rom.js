// 生成 APU 测试 ROM：三个真正的 6502 程序，各自把一个通道开成一个**频率已知**的持续音。
//
//   node tests/make_apu_test_rom.js
//   → tests/roms/apu-pulse.nes     脉冲 1，440 Hz（A4）
//   → tests/roms/apu-triangle.nes  三角波，220 Hz（A3）
//   → tests/roms/apu-noise.nes     噪声（没有单一频率，只验证"有声音"）
//
// 频率怎么来的：
//   脉冲频率   = CPU 主频 / (16 * (timer + 1)) → 1789773 / (16 * 254) ≈ 440.4 Hz
//   三角波频率 = CPU 主频 / (32 * (timer + 1)) → 1789773 / (32 * 254) ≈ 220.2 Hz
// 两者都用 timer = 253（$00FD），正好一个 440、一个 220，方便用数过零点的方式核对。
//
// 每个通道都设成"持续发声"（halt 位置 1，长度计数器不递减），
// 音量用常量 15，这样波形干净、不会因为包络衰减而影响频率统计。

const fs = require("fs");
const path = require("path");

const OUT_DIR = path.join(__dirname, "roms");
const BASE = 0x8000;
const PRG_SIZE = 0x4000;

const TIMER = 253;          // $00FD
const TIMER_LOW = TIMER & 0xFF;
const TIMER_HIGH = (TIMER >> 8) & 0x07;
const LENGTH_HIGH = 0xF8;   // 长度表索引 $1F（halt 置位，不会真的减到 0）

/** 组装一段程序：公共初始化 + 通道配置 + 自循环 */
function buildProgram(channelSetup) {
  const code = [];

  const emit = (...bytes) => {
    for (const b of bytes) code.push(b & 0xFF);
  };

  // ---- 公共初始化 ----
  emit(0x78);                     // SEI
  emit(0xD8);                     // CLD
  emit(0xA2, 0x40, 0x8E, 0x17, 0x40); // LDX #$40 / STX $4017   关帧 IRQ
  emit(0xA2, 0xFF, 0x9A);         // LDX #$FF / TXS
  emit(0xE8);                     // INX → X = 0
  emit(0x8E, 0x00, 0x20);         // STX $2000   关 NMI
  emit(0x8E, 0x01, 0x20);         // STX $2001   关渲染
  emit(0x8E, 0x10, 0x40);         // STX $4010   关 DMC IRQ
  emit(0x8E, 0x15, 0x40);         // STX $4015   先关掉所有通道

  // ---- 通道配置 ----
  for (const [address, value] of channelSetup.writes) {
    emit(0xA9, value, 0x8D, address & 0xFF, address >> 8);   // LDA #value / STA address
  }

  // ---- 自循环 ----
  const loopAddress = BASE + code.length;
  emit(0x4C, loopAddress & 0xFF, (loopAddress >> 8) & 0xFF);  // JMP loop

  return code;
}

function buildChr() {
  const CHR_SIZE = 0x2000;
  const chr = Buffer.alloc(CHR_SIZE);
  for (let tile = 0; tile < CHR_SIZE / 16; tile++) {
    for (let row = 0; row < 8; row++) {
      const base = tile * 16 + row;
      chr[base] = (tile * 8 + row) & 0xFF;
      chr[base + 8] = (((tile >> 2) * 8) + row) & 0xFF;
    }
  }
  return chr;
}

function writeRom(name, code) {
  if (code.length > PRG_SIZE - 6) {
    throw new Error(`${name} 程序太长：${code.length}`);
  }

  const prg = Buffer.alloc(PRG_SIZE, 0xEA);
  Buffer.from(code).copy(prg, 0);

  // 向量表：都指向 $8000
  prg.writeUInt16LE(BASE, PRG_SIZE - 6); // NMI
  prg.writeUInt16LE(BASE, PRG_SIZE - 4); // RESET
  prg.writeUInt16LE(BASE, PRG_SIZE - 2); // IRQ

  const header = Buffer.alloc(16);
  header.write("NES\x1a", 0, "binary");
  header[4] = 1; // 16KB PRG
  header[5] = 1; // 8KB CHR
  header[6] = 0x00; // 水平镜像、mapper 0
  header[7] = 0x00;

  if (!fs.existsSync(OUT_DIR)) fs.mkdirSync(OUT_DIR, { recursive: true });
  const outPath = path.join(OUT_DIR, name);
  fs.writeFileSync(outPath, Buffer.concat([header, prg, buildChr()]));
  console.log(`已生成 ${outPath}（程序 ${code.length} 字节）`);
}

// 脉冲 1：duty=2(50%)、halt、常量音量 15 → $4000 = $BF
// 注意顺序：必须先 $4015 使能通道，再写长度计数器所在的寄存器（$4003）。
// 真机上"通道被禁用时写 $4003 不会装载长度计数器"，所以先使能才是真实游戏的写法。
writeRom(
  "apu-pulse.nes",
  buildProgram({
    writes: [
      [0x4015, 0x01],            // 先使能脉冲 1
      [0x4000, 0xBF],            // duty 50%、halt、常量音量 15
      [0x4001, 0x00],            // 不用扫频
      [0x4002, TIMER_LOW],
      [0x4003, LENGTH_HIGH | TIMER_HIGH],
    ],
  })
);

// 三角波：$4008 = halt + 线性计数器 127，保证一直发声
writeRom(
  "apu-triangle.nes",
  buildProgram({
    writes: [
      [0x4015, 0x04],            // 先使能三角波
      [0x4008, 0xFF],            // halt + 线性计数器重载 127
      [0x400A, TIMER_LOW],
      [0x400B, LENGTH_HIGH | TIMER_HIGH],
    ],
  })
);

// 噪声：halt、常量音量 15、周期索引 4（周期 64，音色偏"沙沙"）
writeRom(
  "apu-noise.nes",
  buildProgram({
    writes: [
      [0x4015, 0x08],            // 先使能噪声
      [0x400C, 0x3F],            // halt + 常量音量 15
      [0x400E, 0x04],            // 周期索引 4、长模式
      [0x400F, LENGTH_HIGH],
    ],
  })
);

// 静音对照：什么都不开，用来确认"没开通道时输出真的是 0"
writeRom(
  "apu-silent.nes",
  buildProgram({
    writes: [
      [0x4015, 0x00],            // 全部关闭
    ],
  })
);

// DMC：让它播一小段采样，播完/关掉之后**必须彻底安静**。
//
// 这条专门抓一个很隐蔽的 bug：关掉 DMC（$4015 bit4 = 0）之后，
// 如果实现里没有"没使能就不走定时器"，残留的移位器会拿着最后一个字节**反复**
// 一位一位地加 2 / 减 2，DAC 就以 DMC 速率一直抖 —— 听感是持续的背景嘶声。
// fogleman 与 jsnes 都在定时器入口直接 return，FCEUX 把 DMCSize 清 0。
//
// 程序流程：配置 DMC → 使能（开始播）→ 空转约 0.55 秒 → $4015 = 0 关掉 → 死循环。
// 判据见 apu_test.ps1：开头要有声音（证明 DMC 真播了），最后 0.5 秒必须是 0。
function buildDmcRom() {
  const code = [];
  const emit = (...bytes) => {
    for (const b of bytes) code.push(b & 0xFF);
  };

  // ---- 公共初始化 ----
  emit(0x78);                          // SEI
  emit(0xD8);                          // CLD
  emit(0xA2, 0x40, 0x8E, 0x17, 0x40);  // LDX #$40 / STX $4017   关帧 IRQ
  emit(0xA2, 0xFF, 0x9A);              // LDX #$FF / TXS
  emit(0xE8);                          // INX → X = 0
  emit(0x8E, 0x00, 0x20);              // STX $2000   关 NMI
  emit(0x8E, 0x01, 0x20);              // STX $2001   关渲染
  emit(0x8E, 0x15, 0x40);              // STX $4015   先关掉所有通道

  // ---- DMC 配置 ----
  emit(0xA9, 0x0F, 0x8D, 0x10, 0x40);  // $4010 = $0F：速率索引 15、不循环、不产生 IRQ
  emit(0xA9, 0x00, 0x8D, 0x12, 0x40);  // $4012 = $00：采样起点 $C000
  emit(0xA9, 0x08, 0x8D, 0x13, 0x40);  // $4013 = $08：长度 129 字节
  emit(0xA9, 0x40, 0x8D, 0x11, 0x40);  // $4011 = $40：DAC 电平 64
  emit(0xA9, 0x10, 0x8D, 0x15, 0x40);  // $4015 = $10：使能 DMC（开始播）

  // ---- 空转约 0.55 秒（三重循环：256 × 256 × 3 次 DEY/BNE）----
  emit(0xA9, 0x03, 0x8D, 0x00, 0x03);  // LDA #$03 / STA $0300
  const outer = code.length;
  emit(0xA2, 0x00);                    // LDX #$00
  const page = code.length;
  emit(0xA0, 0x00);                    // LDY #$00
  const inner = code.length;
  emit(0x88);                          // DEY
  emit(0xD0, (inner - (code.length + 2)) & 0xFF);   // BNE inner
  emit(0xCA);                          // DEX
  emit(0xD0, (page - (code.length + 2)) & 0xFF);    // BNE page
  emit(0xCE, 0x00, 0x03);              // DEC $0300
  emit(0xD0, (outer - (code.length + 2)) & 0xFF);   // BNE outer

  // ---- 关掉 DMC，然后死循环 ----
  emit(0xA9, 0x00, 0x8D, 0x15, 0x40);  // $4015 = $00：关掉所有通道（含 DMC）
  const loop = code.length;
  emit(0x4C, loop & 0xFF, ((BASE + loop) >> 8) & 0xFF);   // JMP loop

  return code;
}

writeRom("apu-dmc-off.nes", buildDmcRom());
