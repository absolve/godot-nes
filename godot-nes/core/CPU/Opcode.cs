namespace GodotNes.Core;

/// <summary>6502 的 13 种寻址模式。</summary>
public enum AddrMode : byte
{
	Imp,    // 隐含       —— 没有操作数（CLC、RTS、TAX…）
	Acc,    // 累加器     —— 操作数就是 A（ASL A、ROR A…）
	Imm,    // 立即       —— 操作数是紧跟的一个字节
	Zp,     // 零页       —— 操作数在 $00XX
	ZpX,    // 零页,X     —— 操作数在 ($XX + X) & $FF
	ZpY,    // 零页,Y
	Abs,    // 绝对       —— 操作数在 $XXXX
	AbsX,   // 绝对,X
	AbsY,   // 绝对,Y
	Ind,    // 间接       —— 指针在 $XXXX（只有 JMP 用）
	IndX,   // (间接,X)   —— 零页指针 + X
	IndY,   // (间接),Y   —— 零页指针，读出的地址再 + Y
	Rel,    // 相对       —— PC + 有符号 8 位偏移（分支）
}

/// <summary>官方 56 条指令，外加一个非法指令占位。</summary>
public enum Instruction : byte
{
	Adc, And, Asl,
	Bcc, Bcs, Beq, Bit, Bmi, Bne, Bpl, Brk, Bvc, Bvs,
	Clc, Cld, Cli, Clv, Cmp, Cpx, Cpy,
	Dec, Dex, Dey,
	Eor, Inc, Inx, Iny, Jmp, Jsr,
	Lda, Ldx, Ldy, Lsr,
	Nop, Ora, Pha, Php, Pla, Plp,
	Rol, Ror, Rti, Rts,
	Sbc, Sec, Sed, Sei, Sta, Stx, Sty,
	Tax, Tay, Tsx, Txa, Txs, Tya,

	/// <summary>非官方指令。不实现，遇到就记录并停下（会在 CPU 窗口里显示出来）。</summary>
	Illegal,
}

/// <summary>一条 opcode 的定义：做什么、怎么取操作数、基础周期数（不含跨页附加）。</summary>
/// <param name="Ins">操作</param>
/// <param name="Mode">寻址模式</param>
/// <param name="Cycles">基础周期数</param>
public readonly record struct Op(Instruction Ins, AddrMode Mode, byte Cycles);
