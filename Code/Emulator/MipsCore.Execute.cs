using System.Runtime.CompilerServices;

namespace PSXEmu;

public partial class MipsCore
{
	/// <summary>Decode and execute a single MIPS-I instruction.</summary>
	private void Execute(uint instr, uint pc, bool inDelaySlot)
	{
		uint op = instr >> 26;         // primary opcode [31:26]
		uint rs = (instr >> 21) & 31; // source reg
		uint rt = (instr >> 16) & 31; // target reg
		uint rd = (instr >> 11) & 31; // dest reg
		uint sa = (instr >> 6) & 31; // shift amount
		uint fn = instr & 63;           // function (R-type)
		uint imm = instr & 0xFFFF;       // immediate (I-type)
		uint si = SignExtend16(imm);  // sign-extended immediate

		switch (op)
		{
			case 0x00: ExecuteSpecial(instr, pc, inDelaySlot, rs, rt, rd, sa, fn); break;
			case 0x01: ExecuteRegimm(instr, pc, rs, rt); break;
			case 0x02: // J
				Branch(JumpTarget(pc, instr));
				break;
			case 0x03: // JAL
				WriteReg(31, pc + 8);
				Branch(JumpTarget(pc, instr));
				break;
			case 0x04: // BEQ
				BranchIf(Gpr[rs] == Gpr[rt], RelBranchTarget(pc, instr));
				break;
			case 0x05: // BNE
				BranchIf(Gpr[rs] != Gpr[rt], RelBranchTarget(pc, instr));
				break;
			case 0x06: // BLEZ
				BranchIf((int)Gpr[rs] <= 0, RelBranchTarget(pc, instr));
				break;
			case 0x07: // BGTZ
				BranchIf((int)Gpr[rs] > 0, RelBranchTarget(pc, instr));
				break;
			case 0x08: // ADDI (overflow trap)
				{
					int a = (int)Gpr[rs], b = (int)si;
					int result = a + b;
					if ((~(a ^ b) & (a ^ result) & 0x80000000) != 0)
						TriggerException(PsxConstants.ExcOvf, pc, inDelaySlot);
					else
						WriteReg(rt, (uint)result);
					break;
				}
			case 0x09: WriteReg(rt, Gpr[rs] + si); break;                                                       // ADDIU
			case 0x0A: WriteReg(rt, (int)Gpr[rs] < (int)si ? 1u : 0u); break;                                   // SLTI
			case 0x0B: WriteReg(rt, Gpr[rs] < si ? 1u : 0u); break;                                              // SLTIU
			case 0x0C: WriteReg(rt, Gpr[rs] & imm); break;                                                      // ANDI (zero-extend)
			case 0x0D: WriteReg(rt, Gpr[rs] | imm); break;                                                      // ORI
			case 0x0E: WriteReg(rt, Gpr[rs] ^ imm); break;                                                      // XORI
			case 0x0F: WriteReg(rt, imm << 16); break;                                                          // LUI

			// Coprocessor ops (COP0-3): each faults Coprocessor-Unusable unless its
			// CU bit is set (COP0 is also usable in kernel mode). COP1/COP3 don't
			// exist on the PSX, when usable they execute as no-ops; only the
			// unusable case faults. Validated by ps1-tests cpu/cop.
			case 0x10: if (CopUsable(0)) ExecuteCop0(instr, pc, inDelaySlot, rs, rt, rd, fn); else RaiseCpU(0, pc, inDelaySlot); break;
			case 0x11: if (!CopUsable(1)) RaiseCpU(1, pc, inDelaySlot); break;                  // COP1 (absent)
			case 0x12: if (CopUsable(2)) ExecuteCop2(instr, rs, rt, rd); else RaiseCpU(2, pc, inDelaySlot); break; // COP2 (GTE)
			case 0x13: if (!CopUsable(3)) RaiseCpU(3, pc, inDelaySlot); break;                  // COP3 (absent)

			// --- Loads ---
			// Size-aware bus costs: each load charges
			// Memory.GetReadCycles(addr, size). Size-aware regions (BIOS/SPU/
			// CDROM/EXP1) return byte/halfword/word-specific costs computed
			// from MEMCTRL defaults.
			// LWL/LWR access an unaligned word but the bus transfers a word
			// at a time, charge as word cost.
			// Writes are pipelined and don't stall, only loads pay.
			// MIPS-I requires alignment: LW must be 4-byte aligned, LH/LHU
			// must be 2-byte aligned. Unaligned accesses raise AdEL.
			case 0x20: { uint _ea = Gpr[rs] + si; WriteRegDelayed(rt, SignExtend8(Memory.ReadByte(_ea))); Cycles += Memory.GetReadCycles(_ea, PsxConstants.BusAccessSize.Byte); break; } // LB
			case 0x21: // LH
				{
					uint _ea = Gpr[rs] + si;
					if ((_ea & 1) != 0) { BadVAddr = _ea; TriggerException(PsxConstants.ExcAdEL, pc, inDelaySlot); break; }
					WriteRegDelayed(rt, SignExtend16(Memory.ReadHalf(_ea)));
					Cycles += Memory.GetReadCycles(_ea, PsxConstants.BusAccessSize.Half);
					break;
				}
			// LWL/LWR merge with the CURRENT $rt value, but bypass the load delay
			// (read the just-loaded value if a load to $rt is pending).
			//
			// Bus cost: an unaligned 32-bit load is the LWL+LWR pair. Charging each
			// the full WORD cost double-counts (the pair would bill 2x an aligned LW).
			// Real hardware bills the pair ~one word's worth of bus time, so charge
			// each the HALF cost -> pair == word. Only affects size-aware MMIO
			// (SPU/CDROM/BIOS/EXP1); RAM is size-independent so the common
			// unaligned-RAM idiom is unchanged, and games never LWL/LWR MMIO.
			// Fixes cpu/access-time SPUCNT 32-bit (was ~82 = 2x40, now ~41 ~= HW 39).
			case 0x22: { uint _ea = Gpr[rs] + si; WriteRegDelayed(rt, LoadWordLeft(_ea, ReadRegLwxBypass(rt))); Cycles += Memory.GetReadCycles(_ea, PsxConstants.BusAccessSize.Half); break; } // LWL
			case 0x23: // LW
				{
					uint _ea = Gpr[rs] + si;
					if ((_ea & 3) != 0) { BadVAddr = _ea; TriggerException(PsxConstants.ExcAdEL, pc, inDelaySlot); break; }
					WriteRegDelayed(rt, Memory.ReadWord(_ea));
					Cycles += Memory.GetReadCycles(_ea, PsxConstants.BusAccessSize.Word);
					break;
				}
			case 0x24: { uint _ea = Gpr[rs] + si; WriteRegDelayed(rt, Memory.ReadByte(_ea)); Cycles += Memory.GetReadCycles(_ea, PsxConstants.BusAccessSize.Byte); break; } // LBU
			case 0x25: // LHU
				{
					uint _ea = Gpr[rs] + si;
					if ((_ea & 1) != 0) { BadVAddr = _ea; TriggerException(PsxConstants.ExcAdEL, pc, inDelaySlot); break; }
					WriteRegDelayed(rt, Memory.ReadHalf(_ea));
					Cycles += Memory.GetReadCycles(_ea, PsxConstants.BusAccessSize.Half);
					break;
				}
			case 0x26: { uint _ea = Gpr[rs] + si; WriteRegDelayed(rt, LoadWordRight(_ea, ReadRegLwxBypass(rt))); Cycles += Memory.GetReadCycles(_ea, PsxConstants.BusAccessSize.Half); break; } // LWR (half cost, see LWL note above)

			// --- Stores ---
			// MIPS-I: SW must be 4-byte aligned, SH must be 2-byte aligned; misaligned raises AdES.
			// SB/SH pass the FULL source register (Gpr[rt]) as well as the masked
			// byte/half: PSX I/O registers ignore byte-enables, so a sub-word
			// store to a 32-bit register (e.g. DMA) latches the whole data-bus
			// word. RAM/scratchpad still use the masked value.
			case 0x28: Memory.WriteByte(Gpr[rs] + si, (byte)Gpr[rt], Gpr[rt]); break; // SB
			case 0x29: // SH
				{
					uint _ea = Gpr[rs] + si;
					if ((_ea & 1) != 0) { BadVAddr = _ea; TriggerException(PsxConstants.ExcAdES, pc, inDelaySlot); break; }
					Memory.WriteHalf(_ea, (ushort)Gpr[rt], Gpr[rt]);
					break;
				}
			case 0x2A: StoreWordLeft(Gpr[rs] + si, Gpr[rt]); break; // SWL
			case 0x2B: // SW
				{
					uint _ea = Gpr[rs] + si;
					if ((_ea & 3) != 0) { BadVAddr = _ea; TriggerException(PsxConstants.ExcAdES, pc, inDelaySlot); break; }
					Memory.WriteWord(_ea, Gpr[rt]);
					break;
				}
			case 0x2E: StoreWordRight(Gpr[rs] + si, Gpr[rt]); break; // SWR

			// LWCz/SWCz: coprocessor load/store. Usability is the raw CU[z] bit
			// (CopEnabled), NO kernel-mode exemption, even for COP0. Only COP2
			// (GTE) has a real transfer path; the 0/1/3 variants are no-ops on the
			// PSX but still fault when their coprocessor is disabled.
			case 0x30: if (!CopEnabled(0)) RaiseCpU(0, pc, inDelaySlot); break; // LWC0
			case 0x31: if (!CopEnabled(1)) RaiseCpU(1, pc, inDelaySlot); break; // LWC1
			case 0x32: // LWC2: load word into GTE data register rt
				{
					if (!CopEnabled(2)) { RaiseCpU(2, pc, inDelaySlot); break; }
					uint addr = Gpr[rs] + si;
					_psx.Gte.WriteData((int)rt, Memory.ReadWord(addr));
					Cycles += Memory.GetReadCycles(addr, PsxConstants.BusAccessSize.Word);
					break;
				}
			case 0x33: if (!CopEnabled(3)) RaiseCpU(3, pc, inDelaySlot); break; // LWC3

			case 0x38: if (!CopEnabled(0)) RaiseCpU(0, pc, inDelaySlot); break; // SWC0
			case 0x39: if (!CopEnabled(1)) RaiseCpU(1, pc, inDelaySlot); break; // SWC1
			case 0x3A: // SWC2: store GTE data register rt to memory.
					   // Stalls until any in-flight GTE op finishes (reads from GTE).
				{
					if (!CopEnabled(2)) { RaiseCpU(2, pc, inDelaySlot); break; }
					StallUntilGteComplete();
					uint addr = Gpr[rs] + si;
					Memory.WriteWord(addr, _psx.Gte.ReadData((int)rt));
					break;
				}
			case 0x3B: if (!CopEnabled(3)) RaiseCpU(3, pc, inDelaySlot); break; // SWC3

			default:
				PsxLog.Write(PsxLogCategory.CPU, PsxLogLevel.Warn, $"Unhandled opcode 0x{op:X2} at 0x{pc:X8} instr=0x{instr:X8}");
				TriggerException(PsxConstants.ExcRI, pc, inDelaySlot);
				// If we're already at the exception vector with an unhandled opcode,
				// the exception handler itself is corrupt, halt to prevent log spam.
				if (pc == 0x80000080 || pc == 0xBFC00180)
				{
					PsxLog.Write(PsxLogCategory.CPU, PsxLogLevel.Error,
						$"[FATAL] Unhandled opcode at exception vector 0x{pc:X8}, exception handler is corrupt! Halting CPU.");
					CrashDetected = true;
					CrashPc = pc;
				}
				break;
		}
	}

	private void ExecuteSpecial(uint instr, uint pc, bool inDelaySlot,
		uint rs, uint rt, uint rd, uint sa, uint fn)
	{
		switch (fn)
		{
			case 0x00: WriteReg(rd, Gpr[rt] << (int)sa); break;                                                // SLL
			case 0x02: WriteReg(rd, Gpr[rt] >> (int)sa); break;                                                // SRL
			case 0x03: WriteReg(rd, (uint)((int)Gpr[rt] >> (int)sa)); break;                                  // SRA
			case 0x04: WriteReg(rd, Gpr[rt] << (int)(Gpr[rs] & 31)); break;                                    // SLLV
			case 0x06: WriteReg(rd, Gpr[rt] >> (int)(Gpr[rs] & 31)); break;                                    // SRLV
			case 0x07: WriteReg(rd, (uint)((int)Gpr[rt] >> (int)(Gpr[rs] & 31))); break;                       // SRAV

			case 0x08: // JR
				Branch(Gpr[rs]);
				break;
			case 0x09: // JALR
				WriteReg(rd, pc + 8);
				Branch(Gpr[rs]);
				break;

			case 0x0C: TriggerException(PsxConstants.ExcSyscall, pc, inDelaySlot); break; // SYSCALL
			case 0x0D: TriggerException(PsxConstants.ExcBreak, pc, inDelaySlot); break; // BREAK

			// MFHI/MFLO/MTHI/MTLO: any access to HI or LO stalls until the
			// in-flight MULT/DIV completes.
			case 0x10: StallUntilMulDivComplete(); WriteReg(rd, Hi); break; // MFHI
			case 0x11: StallUntilMulDivComplete(); Hi = Gpr[rs]; break;     // MTHI
			case 0x12: StallUntilMulDivComplete(); WriteReg(rd, Lo); break; // MFLO
			case 0x13: StallUntilMulDivComplete(); Lo = Gpr[rs]; break;     // MTLO

			// MULT/DIV (Deferred-stall model, replaces the prior
			// "inline `Cycles += 11/36` on every issue"). Pattern:
			//   1. StallUntilMulDivComplete, if a previous MULT/DIV is still
			//      in flight, stall the new issue until it completes.
			//   2. Compute the result and write HI/LO.
			//   3. Stamp `_mulDivCompletionCycle = Cycles + GetMultTicks(...)`
			//      so a subsequent MFHI/MFLO/MTHI/MTLO stalls correctly.
			case 0x18: // MULT
				{
					StallUntilMulDivComplete();
					int s = (int)Gpr[rs];
					long result = (long)s * (int)Gpr[rt];
					Lo = (uint)result;
					Hi = (uint)(result >> 32);
					_mulDivCompletionCycle = Cycles + GetMultTicks(s);
					break;
				}
			case 0x19: // MULTU
				{
					StallUntilMulDivComplete();
					uint u = Gpr[rs];
					ulong result = (ulong)u * Gpr[rt];
					Lo = (uint)result;
					Hi = (uint)(result >> 32);
					_mulDivCompletionCycle = Cycles + GetMultTicksU(u);
					break;
				}
			case 0x1A: // DIV
				{
					StallUntilMulDivComplete();
					if (Gpr[rt] == 0)
					{
						Hi = Gpr[rs];
						Lo = (int)Gpr[rs] >= 0 ? 0xFFFFFFFFu : 1u;
					}
					else if (Gpr[rs] == 0x80000000 && Gpr[rt] == 0xFFFFFFFF)
					{
						Lo = 0x80000000;
						Hi = 0;
					}
					else
					{
						Lo = (uint)((int)Gpr[rs] / (int)Gpr[rt]);
						Hi = (uint)((int)Gpr[rs] % (int)Gpr[rt]);
					}
					_mulDivCompletionCycle = Cycles + GetDivTicks();
					break;
				}
			case 0x1B: // DIVU
				{
					StallUntilMulDivComplete();
					if (Gpr[rt] == 0)
					{
						Lo = 0xFFFFFFFF;
						Hi = Gpr[rs];
					}
					else
					{
						Lo = Gpr[rs] / Gpr[rt];
						Hi = Gpr[rs] % Gpr[rt];
					}
					_mulDivCompletionCycle = Cycles + GetDivTicks();
					break;
				}

			case 0x20: // ADD (overflow trap)
				{
					int a = (int)Gpr[rs], b = (int)Gpr[rt];
					int result = a + b;
					if ((~(a ^ b) & (a ^ result) & unchecked((int)0x80000000)) != 0)
						TriggerException(PsxConstants.ExcOvf, pc, inDelaySlot);
					else
						WriteReg(rd, (uint)result);
					break;
				}
			case 0x21: WriteReg(rd, Gpr[rs] + Gpr[rt]); break; // ADDU
			case 0x22: // SUB (overflow trap)
				{
					int a = (int)Gpr[rs], b = (int)Gpr[rt];
					int result = a - b;
					if (((a ^ b) & (a ^ result) & unchecked((int)0x80000000)) != 0)
						TriggerException(PsxConstants.ExcOvf, pc, inDelaySlot);
					else
						WriteReg(rd, (uint)result);
					break;
				}
			case 0x23: WriteReg(rd, Gpr[rs] - Gpr[rt]); break;                                                  // SUBU
			case 0x24: WriteReg(rd, Gpr[rs] & Gpr[rt]); break;                                                  // AND
			case 0x25: WriteReg(rd, Gpr[rs] | Gpr[rt]); break;                                                  // OR
			case 0x26: WriteReg(rd, Gpr[rs] ^ Gpr[rt]); break;                                                  // XOR
			case 0x27: WriteReg(rd, ~(Gpr[rs] | Gpr[rt])); break;                                              // NOR
			case 0x2A: WriteReg(rd, (int)Gpr[rs] < (int)Gpr[rt] ? 1u : 0u); break;                              // SLT
			case 0x2B: WriteReg(rd, Gpr[rs] < Gpr[rt] ? 1u : 0u); break;                                        // SLTU

			default:
				PsxLog.Write(PsxLogCategory.CPU, PsxLogLevel.Warn, $"Unhandled SPECIAL fn 0x{fn:X2} at 0x{pc:X8}");
				TriggerException(PsxConstants.ExcRI, pc, inDelaySlot);
				break;
		}
	}

	private void ExecuteRegimm(uint instr, uint pc, uint rs, uint rt)
	{
		switch (rt)
		{
			case 0x00: // BLTZ
				BranchIf((int)Gpr[rs] < 0, RelBranchTarget(pc, instr));
				break;
			case 0x01: // BGEZ
				BranchIf((int)Gpr[rs] >= 0, RelBranchTarget(pc, instr));
				break;
			case 0x10: // BLTZAL
			{
				// Snapshot rs BEFORE writing $ra so the condition uses the
				// pre-link value. Critical when rs == 31 ($ra): writing pc+8
				// first would make $ra a positive return-address value, and
				// the `< 0` test would always be false -> branch never taken.
				// Real HW reads the source register first.
				int cond = (int)Gpr[rs];
				WriteReg(31, pc + 8);
				BranchIf(cond < 0, RelBranchTarget(pc, instr));
				break;
			}
			case 0x11: // BGEZAL
			{
				int cond = (int)Gpr[rs];
				WriteReg(31, pc + 8);
				BranchIf(cond >= 0, RelBranchTarget(pc, instr));
				break;
			}
			default:
				PsxLog.Write(PsxLogCategory.CPU, PsxLogLevel.Warn, $"Unhandled REGIMM rt=0x{rt:X2} at 0x{pc:X8}");
				break;
		}
	}

	// R3000A coprocessor-usable test. COPz/LWCz/SWCz raise Coprocessor-Unusable
	// unless CU[z] (SR bit 28+z) is set; COP0 is additionally usable whenever the
	// CPU is in kernel mode (SR.KUc, bit 1, == 0). Per nocash SR spec + the
	// ps1-tests cpu/cop suite.
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool CopUsable(int cop)
	{
		if ((Sr & (1u << (28 + cop))) != 0) return true;
		return cop == 0 && (Sr & 0x2u) == 0; // COP0 usable in kernel mode
	}

	// LWCz/SWCz usability: the raw CU[z] bit only. Unlike the COPz functional
	// instructions, coprocessor load/store does NOT get COP0's kernel-mode
	// exemption, ps1-tests testSwc0Disabled faults in kernel mode with CU0=0.
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool CopEnabled(int cop) => (Sr & (1u << (28 + cop))) != 0;

	// Raise Coprocessor-Unusable (exc 11), recording the offending coprocessor
	// number in CAUSE.CE (bits 28-29). TriggerException clears CE, so set it after.
	private void RaiseCpU(int cop, uint pc, bool inDelaySlot)
	{
		TriggerException(PsxConstants.ExcCpU, pc, inDelaySlot);
		Cause = (Cause & ~0x30000000u) | ((uint)(cop & 3) << 28);
	}

	private void ExecuteCop0(uint instr, uint pc, bool inDelaySlot, uint rs, uint rt, uint rd, uint fn)
	{
		switch (rs)
		{
			case 0x00: // MFC0: rt = cop0[rd] (load delay applies)
				WriteRegDelayed(rt, ReadCop0((int)rd));
				break;
			case 0x04: // MTC0: cop0[rd] = rt
				WriteCop0((int)rd, Gpr[rt]);
				break;
			case 0x10: // Coprocessor instruction
				if (fn == 0x10) ReturnFromException(); // RFE
				break;
			default:
				PsxLog.Write(PsxLogCategory.CPU, PsxLogLevel.Warn, $"Unhandled COP0 rs=0x{rs:X2} at 0x{pc:X8}");
				break;
		}
	}

	// --- GTE (COP2) - Geometry Transform Engine ---

	// GTE command cycle counts.
	private static int GteCycles(uint instr) => (int)(instr & 0x3F) switch
	{
		0x01 => 15, // RTPS  : rotate/translate/perspective single vertex
		0x06 => 8,  // NCLIP : normal clipping
		0x0C => 6,  // OP    : outer product
		0x10 => 8,  // DPCS  : depth cueing single
		0x11 => 7,  // INTPL : interpolation
		0x12 => 8,  // MVMVA : multiply vector by matrix
		0x13 => 19, // NCDS  : normal colour depth cue single
		0x14 => 13, // CDP   : colour depth cue
		0x16 => 44, // NCDT  : normal colour depth cue triple
		0x1B => 17, // NCCS  : normal colour colour single
		0x1C => 11, // CC    : colour colour
		0x1E => 14, // NCS   : normal colour single
		0x20 => 30, // NCT   : normal colour triple
		0x28 => 5,  // SQR   : square of vector
		0x29 => 8,  // DCPL  : depth cue colour light
		0x2A => 17, // DPCT  : depth cueing triple
		0x2D => 5,  // AVSZ3 : average Z value (3 values)
		0x2E => 6,  // AVSZ4 : average Z value (4 values)
		0x30 => 23, // RTPT  : rotate/translate/perspective triple
		0x3D => 5,  // GPF   : general purpose interpolation
		0x3E => 5,  // GPL   : general purpose interpolation with base
		0x3F => 39, // NCCT  : normal colour colour triple
		_ => 6,     // unknown / reserved GTE ops
	};

	// Deferred GTE-stall helper. Real R3000A: the GTE coprocessor runs in
	// PARALLEL with the main pipeline, so a `cop2 <op>` issue costs only 1
	// CPU cycle (the issue itself); the GTE then keeps working for the rest
	// of GteCycles wall-clock cycles. Reads from the GTE (MFC2/CFC2/SWC2)
	// stall the CPU until the coprocessor's completion cycle. Writes
	// (MTC2/CTC2/LWC2) do NOT stall on real HW and are not gated here.
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void StallUntilGteComplete()
	{
		if (Cycles < _gteCompletionCycle) Cycles = _gteCompletionCycle;
	}

	// --- MULT/DIV deferred-stall helpers ---
	// Same pattern as the GTE helpers above. Set the deadline when MULT/DIV
	// issues; stall up to the deadline when MFHI/MFLO/MTHI/MTLO or the next
	// MULT/DIV reads/touches HI/LO.

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void StallUntilMulDivComplete()
	{
		if (Cycles < _mulDivCompletionCycle) Cycles = _mulDivCompletionCycle;
	}

	/// <summary>
	/// 6 / 9 / 13 cycles based on operand magnitude (small operands compute
	/// faster on real R3000A). Minus 1 because the instruction-base cycle
	/// is already paid by Step()'s "++Cycles per instruction" (see GTE
	/// helper for the same offset rationale).
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int GetMultTicks(int rs)
	{
		if (rs < 0)
			return (rs >= -2048) ? (6 - 1) : ((rs >= -1048576) ? (9 - 1) : (13 - 1));
		return (rs < 0x800) ? (6 - 1) : ((rs < 0x100000) ? (9 - 1) : (13 - 1));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int GetMultTicksU(uint rs)
	{
		return (rs < 0x800) ? (6 - 1) : ((rs < 0x100000) ? (9 - 1) : (13 - 1));
	}

	/// <summary>DIV/DIVU cost: 36 cycles minus 1 base = 35.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int GetDivTicks() => 36 - 1;

	private void ExecuteCop2(uint instr, uint rs, uint rt, uint rd)
	{
		uint fmt = (instr >> 25) & 1;
		if (fmt == 1)
		{
			// GTE command (bit 25 = 1): execute GTE operation. The GTE itself
			// can't run two ops concurrently, if a previous op is still in
			// flight, the new cop2 issue stalls until it completes. Then we
			// stamp the new completion cycle.
			//
			// Trace: GTE issues at iter N (user Cycles = N at this point), MFC2
			// stalls in iter N+1.
			//   GTE iter:    stamp _gteCompletionCycle = N + 15 + 1 = N+16
			//                Step's Cycles++ -> N+1
			//   MFC2 iter:   start Cycles = N+1
			//                StallUntilGteComplete -> Cycles = max(N+1, N+16) = N+16
			//                Step's Cycles++ -> N+17
			// Without the `+1` user's MFC2 exits at N+16, one cycle ahead per GTE issue,
			// drift that accumulates over hundreds of GTE ops per frame.
			StallUntilGteComplete();
			_psx.Gte.Execute(instr);
			_gteCompletionCycle = Cycles + GteCycles(instr) + 1;
			return;
		}

		// COP2 register transfer (fmt = 0), rs encodes MFC2/MTC2/CFC2/CTC2
		switch (rs)
		{
			case 0: // MFC2: move from GTE data register to CPU register (load delay applies).
					// Stalls until the GTE op finishes, reading mid-op would return stale data.
				StallUntilGteComplete();
				WriteRegDelayed(rt, _psx.Gte.ReadData((int)rd));
				break;
			case 2: // CFC2: move from GTE control register to CPU register (load delay applies).
					// Same stall semantics as MFC2, control regs include FLAG which the GTE op writes.
				StallUntilGteComplete();
				WriteRegDelayed(rt, _psx.Gte.ReadCtrl((int)rd));
				break;
			case 4: // MTC2: move from CPU register to GTE data register (no stall on real HW)
				_psx.Gte.WriteData((int)rd, Gpr[rt]);
				break;
			case 6: // CTC2: move from CPU register to GTE control register (no stall on real HW)
				_psx.Gte.WriteCtrl((int)rd, Gpr[rt]);
				break;
		}
	}

	// --- Unaligned memory operations ---

	// Returns true when an effective address maps to main RAM (0-0x7FFFFF physical).
	// IsRamAddr removed. The previous load handlers used
	// it to gate a flat `Cycles += 5` for RAM-only access; per-region costs
	// are now derived via Memory.GetReadCycles(addr).

	// LWL: Load Word Left - loads high bytes from aligned word
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private uint LoadWordLeft(uint addr, uint rt)
	{
		uint alignedAddr = addr & ~3u;
		uint shift = (addr & 3) * 8;
		uint mem = Memory.ReadWord(alignedAddr);
		uint mask = 0xFFFFFFFFu << (int)(24 - shift);
		return (rt & ~(mask)) | ((mem << (int)(24 - shift)) & mask);
	}

	// LWR: Load Word Right - loads low bytes from aligned word
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private uint LoadWordRight(uint addr, uint rt)
	{
		uint alignedAddr = addr & ~3u;
		uint shift = (addr & 3) * 8;
		uint mem = Memory.ReadWord(alignedAddr);
		uint mask = 0xFFFFFFFFu >> (int)shift;
		return (rt & ~mask) | ((mem >> (int)shift) & mask);
	}

	// SWL: Store Word Left
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void StoreWordLeft(uint addr, uint rt)
	{
		uint alignedAddr = addr & ~3u;
		uint shift = (addr & 3) * 8;
		uint mem = Memory.ReadWord(alignedAddr);
		uint mask = 0xFFFFFFFFu >> (int)(24 - shift);
		mem = (mem & ~mask) | ((rt >> (int)(24 - shift)) & mask);
		Memory.WriteWord(alignedAddr, mem);
	}

	// SWR: Store Word Right
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void StoreWordRight(uint addr, uint rt)
	{
		uint alignedAddr = addr & ~3u;
		uint shift = (addr & 3) * 8;
		uint mem = Memory.ReadWord(alignedAddr);
		uint mask = 0xFFFFFFFFu << (int)shift;
		mem = (mem & ~mask) | ((rt << (int)shift) & mask);
		Memory.WriteWord(alignedAddr, mem);
	}
}
