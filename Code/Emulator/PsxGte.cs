namespace PSXEmu;

/// <summary>
/// Geometry Transform Engine (GTE / COP2): PSX hardware 3D coprocessor.
/// Implements 24-voice vector math, matrix transforms, perspective projection,
/// and depth-cueing used by virtually every 3D PSX game.
///
/// Register layout:
///   Data [0..31]:  VXY0/VZ0, VXY1/VZ1, VXY2/VZ2, RGBC, OTZ,
///                  IR0..IR3, SXY0..SXYP, SZ0..SZ3, RGB0..RGB2,
///                  MAC0..MAC3, IRGB/ORGB, LZCS/LZCR
///   Control [0..31]: RT matrix, TR vector, LM matrix, BK color,
///                    LC matrix, FC color, OFX/OFY, H, DQA/DQB,
///                    ZSF3/ZSF4, FLAG
/// </summary>
public class PsxGte
{
	// Registers
	private readonly uint[] _data = new uint[32]; // COP2 data registers
	private readonly uint[] _ctrl = new uint[32]; // COP2 control registers

	// 44-bit accumulators held as 64-bit signed
	private long _mac0;
	private long _mac1, _mac2, _mac3;


	// FLAG bits (COP2 control r31)
	private const uint FlagMac1Pos = 1u << 30; // MAC1 positive overflow
	private const uint FlagMac2Pos = 1u << 29;
	private const uint FlagMac3Pos = 1u << 28;
	private const uint FlagMac1Neg = 1u << 27; // MAC1 negative overflow
	private const uint FlagMac2Neg = 1u << 26;
	private const uint FlagMac3Neg = 1u << 25;
	private const uint FlagIr1Sat = 1u << 24; // IR1 saturated
	private const uint FlagIr2Sat = 1u << 23;
	private const uint FlagIr3Sat = 1u << 22;
	private const uint FlagColRSat = 1u << 21; // colour R saturated
	private const uint FlagColGSat = 1u << 20;
	private const uint FlagColBSat = 1u << 19;
	// FLAG bits 15..18, corrected to match real hardware bit positions
	// (Nocash GTE FLAG spec). Previous constants had these four bits scrambled
	// (Pos at 18 instead of 16, Neg at 17 instead of 15, etc.). The error-summary
	// bit 31 still came out right because the union of the same four bits is what feeds it,
	// but per-bit interpretation by games inspecting CFC2(31) was wrong: divide-overflow
	// was reported as MAC0-saturate, MAC0-overflow as SZ3-saturate, etc.
	// Games that retry projection on divide-overflow or branch on MAC0
	// overflow saw the wrong condition.
	private const uint FlagSz3Sat  = 1u << 18; // SZ3/OTZ saturated to 0..0xFFFF
	private const uint FlagDivOver = 1u << 17; // UNR divide overflow (RTPS/RTPT)
	private const uint FlagMac0Pos = 1u << 16; // MAC0 result > +2^31 (positive overflow)
	private const uint FlagMac0Neg = 1u << 15; // MAC0 result < -2^31 (negative underflow)
	private const uint FlagSx2Sat = 1u << 14; // SX2 saturated to -400h..+3FFh (FLAG bit 14)
	private const uint FlagSy2Sat = 1u << 13; // SY2 saturated to -400h..+3FFh (FLAG bit 13)
	private const uint FlagIr0Sat = 1u << 12;

	// UNR reciprocal table (257 entries, exact hardware values)
	// Indexed by ((normalised_divisor & 0x7FFF) + 0x40) >> 7.
	// Entry 256 exists for the (d-7FC0h)/80h == 100h edge case.
	private static readonly byte[] UnrTable =
	{
		0xFF, 0xFD, 0xFB, 0xF9, 0xF7, 0xF5, 0xF3, 0xF1, 0xEF, 0xEE, 0xEC, 0xEA, 0xE8, 0xE6, 0xE4, 0xE3,
		0xE1, 0xDF, 0xDD, 0xDC, 0xDA, 0xD8, 0xD6, 0xD5, 0xD3, 0xD1, 0xD0, 0xCE, 0xCD, 0xCB, 0xC9, 0xC8,
		0xC6, 0xC5, 0xC3, 0xC1, 0xC0, 0xBE, 0xBD, 0xBB, 0xBA, 0xB8, 0xB7, 0xB5, 0xB4, 0xB2, 0xB1, 0xB0,
		0xAE, 0xAD, 0xAB, 0xAA, 0xA9, 0xA7, 0xA6, 0xA4, 0xA3, 0xA2, 0xA0, 0x9F, 0x9E, 0x9C, 0x9B, 0x9A,
		0x99, 0x97, 0x96, 0x95, 0x94, 0x92, 0x91, 0x90, 0x8F, 0x8D, 0x8C, 0x8B, 0x8A, 0x89, 0x87, 0x86,
		0x85, 0x84, 0x83, 0x82, 0x81, 0x7F, 0x7E, 0x7D, 0x7C, 0x7B, 0x7A, 0x79, 0x78, 0x77, 0x75, 0x74,
		0x73, 0x72, 0x71, 0x70, 0x6F, 0x6E, 0x6D, 0x6C, 0x6B, 0x6A, 0x69, 0x68, 0x67, 0x66, 0x65, 0x64,
		0x63, 0x62, 0x61, 0x60, 0x5F, 0x5E, 0x5D, 0x5D, 0x5C, 0x5B, 0x5A, 0x59, 0x58, 0x57, 0x56, 0x55,
		0x54, 0x53, 0x53, 0x52, 0x51, 0x50, 0x4F, 0x4E, 0x4D, 0x4D, 0x4C, 0x4B, 0x4A, 0x49, 0x48, 0x48,
		0x47, 0x46, 0x45, 0x44, 0x43, 0x43, 0x42, 0x41, 0x40, 0x3F, 0x3F, 0x3E, 0x3D, 0x3C, 0x3C, 0x3B,
		0x3A, 0x39, 0x39, 0x38, 0x37, 0x36, 0x36, 0x35, 0x34, 0x33, 0x33, 0x32, 0x31, 0x31, 0x30, 0x2F,
		0x2E, 0x2E, 0x2D, 0x2C, 0x2C, 0x2B, 0x2A, 0x2A, 0x29, 0x28, 0x28, 0x27, 0x26, 0x26, 0x25, 0x24,
		0x24, 0x23, 0x22, 0x22, 0x21, 0x20, 0x20, 0x1F, 0x1E, 0x1E, 0x1D, 0x1D, 0x1C, 0x1B, 0x1B, 0x1A,
		0x19, 0x19, 0x18, 0x18, 0x17, 0x16, 0x16, 0x15, 0x15, 0x14, 0x14, 0x13, 0x12, 0x12, 0x11, 0x11,
		0x10, 0x0F, 0x0F, 0x0E, 0x0E, 0x0D, 0x0D, 0x0C, 0x0C, 0x0B, 0x0A, 0x0A, 0x09, 0x09, 0x08, 0x08,
		0x07, 0x07, 0x06, 0x06, 0x05, 0x05, 0x04, 0x04, 0x03, 0x03, 0x02, 0x02, 0x01, 0x01, 0x00, 0x00,
		0x00,
	};


	public void Reset()
	{
		Array.Clear(_data);
		Array.Clear(_ctrl);
		_mac0 = _mac1 = _mac2 = _mac3 = 0;
		_rawMac3 = 0;
	}

	// ---- Save-state ---- (UnrTable is a constant LUT, excluded.)
	public void SaveState(StateWriter w)
	{
		w.UInts(_data);
		w.UInts(_ctrl);
		w.S64(_mac0); w.S64(_mac1); w.S64(_mac2); w.S64(_mac3);
		w.S64(_rawMac3);
	}

	public void LoadState(StateReader r)
	{
		r.UInts(_data);
		r.UInts(_ctrl);
		_mac0 = r.S64(); _mac1 = r.S64(); _mac2 = r.S64(); _mac3 = r.S64();
		_rawMac3 = r.S64();
	}

	// Public register interface (MFC2/MTC2/CFC2/CTC2 dispatch)

	public uint ReadData(int r)
	{
		return r switch
		{
			15 => _data[14],              // SXYP reads SXY2
			28 or 29 => BuildOrgb(),      // IRGB/ORGB: pack IR1-3 into 5-bit fields
			31 => (uint)LeadingZeroCount((int)_data[30]),
			_ => _data[r],
		};
	}

	public void WriteData(int r, uint val)
	{
		switch (r)
		{
			case 1:
			case 3:
			case 5:
			case 8:
			case 9:
			case 10:
			case 11:
				_data[r] = (uint)(int)(short)(val & 0xFFFF);
				break;
			case 7:
			case 16:
			case 17:
			case 18:
			case 19:
				_data[r] = val & 0xFFFF;
				break;
			case 15: // SXYP: push SXY FIFO
				_data[12] = _data[13];
				_data[13] = _data[14];
				_data[14] = val;
				break;
			case 28: // IRGB: unpack 5-bit R/G/B into IR1-3
				_data[28] = val;
				_data[9] = (uint)((short)((val & 0x1F) << 7));
				_data[10] = (uint)((short)(((val >> 5) & 0x1F) << 7));
				_data[11] = (uint)((short)(((val >> 10) & 0x1F) << 7));
				break;
			case 29: break; // ORGB: read-only
			case 31: break; // LZCR: read-only
			default:
				_data[r] = val;
				break;
		}
	}

	public uint ReadCtrl(int r)
	{
		if (r == 31)
		{
			// Bit 31 = error summary (OR of bits 30-23 and 18-13)
			uint f = _ctrl[31] & 0x7FFFF000u;
			if ((f & 0x7F87E000u) != 0) f |= 0x80000000u;
			return f;
		}
		return _ctrl[r];
	}

	public void WriteCtrl(int r, uint val)
	{
		if (r == 31)
		{
			// FLAG (cop2r63): bits 12-30 are directly writable via CTC2; bits 0-11
			// are always 0 and bit 31 (error summary) is recomputed on read. Real
			// hardware DOES let you write it, used by save-states and exercised by
			// the ps1-tests gte register suite (which writes a pattern then reads
			// it back). Treating it as read-only made every register test mismatch
			// on reg 63 and blocked all the opcode tests.
			_ctrl[31] = val & 0x7FFFF000u;
			return;
		}
		_ctrl[r] = r switch
		{
			4 or 12 or 20 or 26 or 27 or 29 or 30 => (uint)(int)(short)(val & 0xFFFF),
			_ => val,
		};
	}

	// GTE command execution

	public void Execute(uint cmd)
	{
		_ctrl[31] = 0;     // clear FLAG before each command
		_mac0 = _mac1 = _mac2 = _mac3 = 0;

		bool sf = (cmd & (1u << 19)) != 0; // shift factor: right-shift MAC1-3 by 12
		bool lm = (cmd & (1u << 10)) != 0; // lower limit: saturate IR to 0..7FFF

		switch (cmd & 0x3F)
		{
			case 0x01: CmdRtps(sf, lm, 0, true); break; // RTPS
			case 0x06: CmdNclip(); break;                // NCLIP
			case 0x0C: CmdOp(sf, lm); break;          // OP
			case 0x10: CmdDpcs(sf, lm); break;        // DPCS
			case 0x11: CmdIntpl(sf, lm); break;       // INTPL
			case 0x12: CmdMvmva(cmd, sf, lm); break;  // MVMVA
			case 0x13: CmdNcds(sf, lm); break;        // NCDS
			case 0x14: CmdCdp(sf, lm); break;         // CDP
			case 0x16: CmdNcdt(sf, lm); break;        // NCDT
			case 0x1B: CmdNccs(sf, lm); break;        // NCCS
			case 0x1C: CmdCc(sf, lm); break;          // CC
			case 0x1E: CmdNcs(sf, lm); break;         // NCS
			case 0x20: CmdNct(sf, lm); break;         // NCT
			case 0x28: CmdSqr(sf, lm); break;         // SQR
			case 0x29: CmdDcpl(sf, lm); break;        // DCPL
			case 0x2A: CmdDpct(sf, lm); break;        // DPCT
			case 0x2D: CmdAvsz3(); break;                // AVSZ3
			case 0x2E: CmdAvsz4(); break;                // AVSZ4
			case 0x30: CmdRtpt(sf, lm); break;        // RTPT
			case 0x3D: CmdGpf(sf, lm); break;         // GPF
			case 0x3E: CmdGpl(sf, lm); break;         // GPL
			case 0x3F: CmdNcct(sf, lm); break;        // NCCT
		}

		// Update error summary in FLAG
		uint flag = _ctrl[31] & 0x7FFFF000u;
		if ((flag & 0x7F87E000u) != 0) flag |= 0x80000000u;
		_ctrl[31] = flag;
	}

	// GTE commands

	// RTPS: Rotate, Translate, Perspective-transform a single vertex. `last` is
	// true for a standalone RTPS and only for the THIRD vertex of RTPT: the
	// depth-cue (MAC0 + IR0) is computed for the last vertex only, so its
	// saturation flags don't accumulate across all three vertices of an RTPT.
	private void CmdRtps(bool sf, bool lm, int v, bool last)
	{
		GetVector(v, out short vx, out short vy, out short vz);
		MultiplyMatrixVector(MatBase.RT, TrBase.TR, vx, vy, vz, sf);

		int ir1 = SetIr1((int)_mac1, lm);
		int ir2 = SetIr2((int)_mac2, lm);
		// RTPS/RTPT quirk: IR3's value clamps the (sf-shifted) MAC3 with lm, but the
		// saturation FLAG (bit 22) is set from the UNSHIFTED accumulator (MAC3>>12)
		// versus the signed-16 range, regardless of lm.
		SetIr3Rtp((int)_mac3, _rawMac3, lm);

		// SZ FIFO: SZ3 = (MAC3 accumulator >> 12), TRUNCATED to s32 BEFORE the
		// unsigned-saturate to 0..FFFF. Truncating first can flip the sign when
		// bit 31 of the shifted value is set, saturating SZ3 to 0 instead of FFFF,
		// and then the UNR divide sees SZ3=0 and overflows. Doing the clamp on the
		// full 64-bit value (as before) never sees that sign flip. _rawMac3 holds
		// the pre-sf-shift accumulator.
		int sz3val = (int)(_rawMac3 >> 12);
		_data[16] = _data[17];
		_data[17] = _data[18];
		_data[18] = _data[19];
		uint sz3 = (uint)Math.Clamp(sz3val, 0, 0xFFFF);
		if (sz3val < 0 || sz3val > 0xFFFF) _ctrl[31] |= FlagSz3Sat;
		_data[19] = sz3;

		uint quotient = UNRDivide(_ctrl[26] & 0xFFFF, sz3);

		// SX2, SY2: the screen coords are derived from the FULL (untruncated) 64-bit
		// MAC0 result shifted right by 16, NOT from the 32-bit-truncated MAC0
		// register. MAC0 is still stored truncated for read-back, but truncating
		// before the shift would discard the high bits and flip the sign on overflow.
		int ofx = (int)_ctrl[24];
		int ofy = (int)_ctrl[25];
		long fx = (long)ir1 * quotient + ofx;
		SetMac0(fx);
		int sx2 = SetSx2((int)(fx >> 16));
		long fy = (long)ir2 * quotient + ofy;
		SetMac0(fy);
		int sy2 = SetSy2((int)(fy >> 16));
		PushSXY(sx2, sy2);


		// Depth cue (last vertex only): MAC0 = DQB + DQA*quotient, IR0 = MAC0/1000h,
		// derived from the full result >> 12. For RTPT the first two vertices push
		// SX/SY/SZ but compute no depth or IR0 (so IR0-sat doesn't accumulate).
		if (last)
		{
			int dqa = (int)(short)(_ctrl[27] & 0xFFFF);
			long dqb = (long)(int)_ctrl[28];
			long f0 = dqb + (long)dqa * quotient;
			SetMac0(f0);
			SetIr0((int)(f0 >> 12));
		}
	}

	// NCLIP: Normal clip, compute cross product of SXY0,1,2 -> MAC0
	private void CmdNclip()
	{
		int sx0 = (short)(_data[12] & 0xFFFF);
		int sy0 = (short)(_data[12] >> 16);
		int sx1 = (short)(_data[13] & 0xFFFF);
		int sy1 = (short)(_data[13] >> 16);
		int sx2 = (short)(_data[14] & 0xFFFF);
		int sy2 = (short)(_data[14] >> 16);

		long result = (long)sx0 * (sy1 - sy2) + (long)sx1 * (sy2 - sy0) + (long)sx2 * (sy0 - sy1);
		SetMac0(result);
	}

	// OP: Outer product of [D1,D2,D3] x [IR1,IR2,IR3]
	private void CmdOp(bool sf, bool lm)
	{
		int d1 = (short)(_ctrl[0] & 0xFFFF); // RT11
		int d2 = (short)(_ctrl[2] & 0xFFFF); // RT22
		int d3 = (short)(_ctrl[4] & 0xFFFF); // RT33
		int ir1 = (short)(_data[9] & 0xFFFF);
		int ir2 = (short)(_data[10] & 0xFFFF);
		int ir3 = (short)(_data[11] & 0xFFFF);

		SetMac123(
			(long)d2 * ir3 - (long)d3 * ir2,
			(long)d3 * ir1 - (long)d1 * ir3,
			(long)d1 * ir2 - (long)d2 * ir1,
			sf);
		SetIr1((int)_mac1, lm);
		SetIr2((int)_mac2, lm);
		SetIr3((int)_mac3, lm);
	}

	// DPCS: Depth-cue a single colour (RGBC)
	private void CmdDpcs(bool sf, bool lm)
	{
		int r = (int)(_data[6] & 0xFF);
		int g = (int)((_data[6] >> 8) & 0xFF);
		int b = (int)((_data[6] >> 16) & 0xFF);
		DoCdp(r, g, b, sf, lm);
	}

	// INTPL: Interpolate IR with FC
	private void CmdIntpl(bool sf, bool lm)
	{
		// INTPL: base value in [1,27,12] is IRn << 12, then the SAME two-step
		// interpolation toward FC as DPCS (was a fused single step, same bug).
		int ir1 = (short)(_data[9] & 0xFFFF);
		int ir2 = (short)(_data[10] & 0xFFFF);
		int ir3 = (short)(_data[11] & 0xFFFF);
		InterpolateColor((long)ir1 << 12, (long)ir2 << 12, (long)ir3 << 12, sf, lm);
		PushRgb((int)_mac1 >> 4, (int)_mac2 >> 4, (int)_mac3 >> 4, (int)(_data[6] >> 24));
	}

	// MVMVA: Multiply vector by matrix and add vector
	private void CmdMvmva(uint cmd, bool sf, bool lm)
	{
		int mm = (int)((cmd >> 17) & 3); // matrix: 0=RT, 1=LM, 2=LC
		int mv = (int)((cmd >> 15) & 3); // input vector: 0=V0, 1=V1, 2=V2, 3=IR
		int tv = (int)((cmd >> 13) & 3); // translation: 0=TR, 1=BK, 2=FC(buggy), 3=none

		// Read input vector
		short vx, vy, vz;
		if (mv < 3)
		{
			GetVector(mv, out vx, out vy, out vz);
		}
		else
		{
			vx = (short)(_data[9] & 0xFFFF);
			vy = (short)(_data[10] & 0xFFFF);
			vz = (short)(_data[11] & 0xFFFF);
		}

		// Read matrix base (mm=3 selects the "buggy matrix"; not modelled yet)
		int mBase = mm switch { 0 => 0, 1 => 8, 2 => 16, _ => 0 };
		long m00 = GetMatElem(mBase, 0), m01 = GetMatElem(mBase, 1), m02 = GetMatElem(mBase, 2);
		long m10 = GetMatElem(mBase, 3), m11 = GetMatElem(mBase, 4), m12 = GetMatElem(mBase, 5);
		long m20 = GetMatElem(mBase, 6), m21 = GetMatElem(mBase, 7), m22 = GetMatElem(mBase, 8);

		if (mm == 3)
		{
			// "Buggy matrix": hardware builds a degenerate matrix from RGBC.R<<4,
			// IR0 and two RT elements:
			// row0 = [-(R<<4), (R<<4), IR0],  row1 = [RT13]*3,  row2 = [RT22]*3
			long r4 = (short)((_data[6] & 0xFF) << 4);
			long ir0 = (short)(_data[8] & 0xFFFF);
			long rt13 = GetMatElem(0, 2); // RT(0,2)
			long rt22 = GetMatElem(0, 4); // RT(1,1)
			m00 = -r4; m01 = r4; m02 = ir0;
			m10 = rt13; m11 = rt13; m12 = rt13;
			m20 = rt22; m21 = rt22; m22 = rt22;
		}

		// Translation vector (sign-extended 32-bit; FC is tv=2 = the buggy path).
		long t0, t1, t2;
		switch (tv)
		{
			case 0: t0 = (int)_ctrl[5]; t1 = (int)_ctrl[6]; t2 = (int)_ctrl[7]; break;    // TR
			case 1: t0 = (int)_ctrl[13]; t1 = (int)_ctrl[14]; t2 = (int)_ctrl[15]; break; // BK
			case 2: t0 = (int)_ctrl[21]; t1 = (int)_ctrl[22]; t2 = (int)_ctrl[23]; break; // FC
			default: t0 = t1 = t2 = 0; break;
		}

		if (tv != 2)
		{
			// Normal: MAC = (T<<12) + M*V
			SetMac123(
				(t0 << 12) + m00 * vx + m01 * vy + m02 * vz,
				(t1 << 12) + m10 * vx + m11 * vy + m12 * vz,
				(t2 << 12) + m20 * vx + m21 * vy + m22 * vz,
				sf);
			SetIr1((int)_mac1, lm);
			SetIr2((int)_mac2, lm);
			SetIr3((int)_mac3, lm);
		}
		else
		{
			// FC-translation hardware bug: IR is set from (FC<<12 + M(i,0)*Vx) >> shift
			// with lm=false (first product only), then MAC is OVERWRITTEN with the remaining two products
			// (M(i,1)*Vy + M(i,2)*Vz) >> shift and IR re-clamped with lm. The FC
			// term + first product survive only in the flags and the discarded IR.
			SetMac123((t0 << 12) + m00 * vx, (t1 << 12) + m10 * vx, (t2 << 12) + m20 * vx, sf);
			SetIr1((int)_mac1, false);
			SetIr2((int)_mac2, false);
			SetIr3((int)_mac3, false);

			SetMac123(m01 * vy + m02 * vz, m11 * vy + m12 * vz, m21 * vy + m22 * vz, sf);
			SetIr1((int)_mac1, lm);
			SetIr2((int)_mac2, lm);
			SetIr3((int)_mac3, lm);
		}
	}

	// NCDS: Normal colour, depth cue (single, V0)
	private void CmdNcds(bool sf, bool lm) => DoNcds(sf, lm, 0);

	private void DoNcds(bool sf, bool lm, int v)
	{
		// Step 1: light calculation, LM * Vn
		GetVector(v, out short vx, out short vy, out short vz);
		MultiplyMatrixVector(MatBase.LM, TrBase.None, vx, vy, vz, sf);
		SetIr1((int)_mac1, lm);
		SetIr2((int)_mac2, lm);
		SetIr3((int)_mac3, lm);

		// Step 2: colour calculation, BK + LC * IR
		MultiplyColorMatrix(sf, lm);

		// Step 3: [R*IR1,G*IR2,B*IR3] SHL 4 -> depth-cue with FC
		int r = (int)(_data[6] & 0xFF);
		int g = (int)((_data[6] >> 8) & 0xFF);
		int b = (int)((_data[6] >> 16) & 0xFF);
		long inMac1 = (long)r * (short)(_data[9] & 0xFFFF) << 4;
		long inMac2 = (long)g * (short)(_data[10] & 0xFFFF) << 4;
		long inMac3 = (long)b * (short)(_data[11] & 0xFFFF) << 4;
		InterpolateColor(inMac1, inMac2, inMac3, sf, lm);
		PushRgb((int)_mac1 >> 4, (int)_mac2 >> 4, (int)_mac3 >> 4, (int)(_data[6] >> 24));
	}

	// CDP: Colour depth queue
	// = CC step (BK + LC * IR) followed by depth-cue interpolation with RGBC
	private void CmdCdp(bool sf, bool lm)
	{
		MultiplyColorMatrix(sf, lm);
		int r = (int)(_data[6] & 0xFF);
		int g = (int)((_data[6] >> 8) & 0xFF);
		int b = (int)((_data[6] >> 16) & 0xFF);
		long inMac1 = (long)r * (short)(_data[9] & 0xFFFF) << 4;
		long inMac2 = (long)g * (short)(_data[10] & 0xFFFF) << 4;
		long inMac3 = (long)b * (short)(_data[11] & 0xFFFF) << 4;
		InterpolateColor(inMac1, inMac2, inMac3, sf, lm);
		PushRgb((int)_mac1 >> 4, (int)_mac2 >> 4, (int)_mac3 >> 4, (int)(_data[6] >> 24));
	}

	// NCDT: Normal colour, depth cue (triple)
	private void CmdNcdt(bool sf, bool lm)
	{
		DoNcds(sf, lm, 0);
		DoNcds(sf, lm, 1);
		DoNcds(sf, lm, 2);
	}

	// NCCS: Normal colour colour (single)
	private void CmdNccs(bool sf, bool lm) => DoNccs(sf, lm, 0);

	private void DoNccs(bool sf, bool lm, int v)
	{
		GetVector(v, out short vx, out short vy, out short vz);
		MultiplyMatrixVector(MatBase.LM, TrBase.None, vx, vy, vz, sf);
		SetIr1((int)_mac1, lm);
		SetIr2((int)_mac2, lm);
		SetIr3((int)_mac3, lm);
		MultiplyColorMatrix(sf, lm);
		int r = (int)(_data[6] & 0xFF);
		int g = (int)((_data[6] >> 8) & 0xFF);
		int b = (int)((_data[6] >> 16) & 0xFF);
		// PSX hardware: [R*IR1, G*IR2, B*IR3] SHL 4, then SAR (sf*12)
		int ir1s3 = (short)(_data[9] & 0xFFFF);
		int ir2s3 = (short)(_data[10] & 0xFFFF);
		int ir3s3 = (short)(_data[11] & 0xFFFF);
		SetMac123(
			(long)r * ir1s3 << 4,
			(long)g * ir2s3 << 4,
			(long)b * ir3s3 << 4,
			sf);
		SetIr1((int)_mac1, lm);
		SetIr2((int)_mac2, lm);
		SetIr3((int)_mac3, lm);
		PushRgb((int)_mac1 >> 4, (int)_mac2 >> 4, (int)_mac3 >> 4, (int)(_data[6] >> 24));
	}

	// CC: Colour colour
	private void CmdCc(bool sf, bool lm)
	{
		MultiplyColorMatrix(sf, lm);
		int r = (int)(_data[6] & 0xFF);
		int g = (int)((_data[6] >> 8) & 0xFF);
		int b = (int)((_data[6] >> 16) & 0xFF);
		// PSX hardware: [R*IR1, G*IR2, B*IR3] SHL 4, then SAR (sf*12)
		SetMac123(
			(long)r * (short)(_data[9] & 0xFFFF) << 4,
			(long)g * (short)(_data[10] & 0xFFFF) << 4,
			(long)b * (short)(_data[11] & 0xFFFF) << 4,
			sf);
		SetIr1((int)_mac1, lm);
		SetIr2((int)_mac2, lm);
		SetIr3((int)_mac3, lm);
		PushRgb((int)_mac1 >> 4, (int)_mac2 >> 4, (int)_mac3 >> 4, (int)(_data[6] >> 24));
	}

	// NCS: Normal colour (single)
	private void CmdNcs(bool sf, bool lm) => DoNcs(sf, lm, 0);

	private void DoNcs(bool sf, bool lm, int v)
	{
		GetVector(v, out short vx, out short vy, out short vz);
		MultiplyMatrixVector(MatBase.LM, TrBase.None, vx, vy, vz, sf);
		SetIr1((int)_mac1, lm);
		SetIr2((int)_mac2, lm);
		SetIr3((int)_mac3, lm);
		MultiplyColorMatrix(sf, lm);
		SetIr1((int)_mac1, lm);
		SetIr2((int)_mac2, lm);
		SetIr3((int)_mac3, lm);
		PushRgb((int)_mac1 >> 4, (int)_mac2 >> 4, (int)_mac3 >> 4, (int)(_data[6] >> 24));
	}

	// NCT: Normal colour (triple)
	private void CmdNct(bool sf, bool lm)
	{
		DoNcs(sf, lm, 0);
		DoNcs(sf, lm, 1);
		DoNcs(sf, lm, 2);
	}

	// SQR: Square of IR vector
	private void CmdSqr(bool sf, bool lm)
	{
		int ir1 = (short)(_data[9] & 0xFFFF);
		int ir2 = (short)(_data[10] & 0xFFFF);
		int ir3 = (short)(_data[11] & 0xFFFF);
		SetMac123((long)ir1 * ir1, (long)ir2 * ir2, (long)ir3 * ir3, sf);
		SetIr1((int)_mac1, lm);
		SetIr2((int)_mac2, lm);
		SetIr3((int)_mac3, lm);
	}

	// DCPL: Depth cue colour light
	private void CmdDcpl(bool sf, bool lm)
	{
		// DCPL: base colour in = (RGBC channel * IRn) << 4, then the SAME two-step
		// depth-cue toward FC as DPCS/INTPL (was a fused single step, same bug).
		int r = (int)(_data[6] & 0xFF);
		int g = (int)((_data[6] >> 8) & 0xFF);
		int b = (int)((_data[6] >> 16) & 0xFF);
		int ir1 = (short)(_data[9] & 0xFFFF);
		int ir2 = (short)(_data[10] & 0xFFFF);
		int ir3 = (short)(_data[11] & 0xFFFF);
		InterpolateColor(((long)r * ir1) << 4, ((long)g * ir2) << 4, ((long)b * ir3) << 4, sf, lm);
		PushRgb((int)_mac1 >> 4, (int)_mac2 >> 4, (int)_mac3 >> 4, (int)(_data[6] >> 24));
	}

	// DPCT: Depth cue (triple, uses RGB0/1/2 FIFO)
	private void CmdDpct(bool sf, bool lm)
	{
		for (int i = 0; i < 3; i++)
		{
			int r = (int)(_data[20] & 0xFF);
			int g = (int)((_data[20] >> 8) & 0xFF);
			int b = (int)((_data[20] >> 16) & 0xFF);
			DoCdp(r, g, b, sf, lm);
		}
	}

	// AVSZ3: Average of SZ1, SZ2, SZ3
	// OTZ = clamp(value, 0, FFFFh); saturation sets the SZ3/OTZ flag (bit 18). The
	// caller passes (full result >> 12), NOT (s32-truncated MAC0 >> 12), truncating
	// first flips the sign for results that overflow 32 bits (e.g. ZSF*-0x8000 * a
	// large SZ sum), making OTZ saturate high instead of clamping to 0.
	private void SetOtz(int value)
	{
		int clamped = Math.Clamp(value, 0, 0xFFFF);
		if (clamped != value) _ctrl[31] |= FlagSz3Sat;
		_data[7] = (uint)clamped;
	}

	private void CmdAvsz3()
	{
		long zsf3 = (short)(_ctrl[29] & 0xFFFF);
		long sum = (long)(_data[17] & 0xFFFF) + (long)(_data[18] & 0xFFFF) + (long)(_data[19] & 0xFFFF);
		long result = zsf3 * sum;
		SetMac0(result);
		SetOtz((int)(result >> 12));
	}

	// AVSZ4: Average of SZ0, SZ1, SZ2, SZ3
	private void CmdAvsz4()
	{
		long zsf4 = (short)(_ctrl[30] & 0xFFFF);
		long sum = (long)(_data[16] & 0xFFFF) + (long)(_data[17] & 0xFFFF)
				   + (long)(_data[18] & 0xFFFF) + (long)(_data[19] & 0xFFFF);
		long result = zsf4 * sum;
		SetMac0(result);
		SetOtz((int)(result >> 12));
	}

	// RTPT: Rotate, translate, project (V0, V1, V2)
	private void CmdRtpt(bool sf, bool lm)
	{
		CmdRtps(sf, lm, 0, false);
		CmdRtps(sf, lm, 1, false);
		CmdRtps(sf, lm, 2, true);
	}

	// GPF: General-purpose interpolation (factor = IR0)
	private void CmdGpf(bool sf, bool lm)
	{
		int ir0 = (short)(_data[8] & 0xFFFF);
		int ir1 = (short)(_data[9] & 0xFFFF);
		int ir2 = (short)(_data[10] & 0xFFFF);
		int ir3 = (short)(_data[11] & 0xFFFF);

		SetMac123((long)ir0 * ir1, (long)ir0 * ir2, (long)ir0 * ir3, sf);
		SetIr1((int)_mac1, lm);
		SetIr2((int)_mac2, lm);
		SetIr3((int)_mac3, lm);
		PushRgb((int)_mac1 >> 4, (int)_mac2 >> 4, (int)_mac3 >> 4, (int)(_data[6] >> 24));
	}

	// GPL: General-purpose interpolation (add to MAC)
	private void CmdGpl(bool sf, bool lm)
	{
		int ir0 = (short)(_data[8] & 0xFFFF);
		int ir1 = (short)(_data[9] & 0xFFFF);
		int ir2 = (short)(_data[10] & 0xFFFF);
		int ir3 = (short)(_data[11] & 0xFFFF);

		long shift = sf ? 12 : 0;
		SetMac123(
			((long)(int)_data[25] << (int)shift) + (long)ir0 * ir1,
			((long)(int)_data[26] << (int)shift) + (long)ir0 * ir2,
			((long)(int)_data[27] << (int)shift) + (long)ir0 * ir3,
			sf);
		SetIr1((int)_mac1, lm);
		SetIr2((int)_mac2, lm);
		SetIr3((int)_mac3, lm);
		PushRgb((int)_mac1 >> 4, (int)_mac2 >> 4, (int)_mac3 >> 4, (int)(_data[6] >> 24));
	}

	// NCCT: Normal colour colour (triple)
	private void CmdNcct(bool sf, bool lm)
	{
		DoNccs(sf, lm, 0);
		DoNccs(sf, lm, 1);
		DoNccs(sf, lm, 2);
	}

	// Common sub-operations

	// Matrix base selector
	private enum MatBase { RT = 0, LM = 8, LC = 16 }
	private enum TrBase { TR = 5, BK = 13, FC = 21, None = -1 }

	// --- GTE 44-bit per-term overflow ---
	// After each intermediate partial sum, the MAC accumulator is range-checked
	// (sets the component's pos/neg FLAG bit) and sign-extended (wrapped) to 44
	// bits. A multi-term matrix*vector sum can therefore set BOTH the pos AND neg
	// flags if it swings across the 44-bit boundary mid-accumulation, which a
	// single final-only check misses. The wrap only affects bits >=44, which are
	// discarded when MAC truncates to 32 bits, so non-overflowing results (and the
	// stored MAC values themselves) are unchanged; only the FLAG bits gain accuracy.
	private long SignExtendMac(int comp, long value)
	{
		if (value < Mac43BitMin)
			_ctrl[31] |= comp == 1 ? FlagMac1Neg : comp == 2 ? FlagMac2Neg : FlagMac3Neg;
		else if (value > Mac43BitMax)
			_ctrl[31] |= comp == 1 ? FlagMac1Pos : comp == 2 ? FlagMac2Pos : FlagMac3Pos;
		const long mask44 = (1L << 44) - 1;
		long v = value & mask44;
		if ((v & (1L << 43)) != 0) v |= ~mask44; // sign-extend bit 43
		return v;
	}

	// Matrix*vector WITH translation: SignExt(SignExt(T + p0) + p1) + p2.
	private long MacChainT(int comp, long t, long p0, long p1, long p2)
	{
		long acc = SignExtendMac(comp, t + p0);
		acc = SignExtendMac(comp, acc + p1);
		return acc + p2;
	}

	// Matrix*vector WITHOUT translation: SignExt(p0 + p1) + p2.
	private long MacChain(int comp, long p0, long p1, long p2)
		=> SignExtendMac(comp, p0 + p1) + p2;

	private void MultiplyMatrixVector(MatBase mat, TrBase tr, short vx, short vy, short vz, bool sf)
	{
		int mBase = (int)mat;
		long m11 = GetMatElem(mBase, 0), m12 = GetMatElem(mBase, 1), m13 = GetMatElem(mBase, 2);
		long m21 = GetMatElem(mBase, 3), m22 = GetMatElem(mBase, 4), m23 = GetMatElem(mBase, 5);
		long m31 = GetMatElem(mBase, 6), m32 = GetMatElem(mBase, 7), m33 = GetMatElem(mBase, 8);

		if (tr != TrBase.None)
		{
			int trBase = (int)tr;
			long tx = (long)(int)_ctrl[trBase] << 12;
			long ty = (long)(int)_ctrl[trBase + 1] << 12;
			long tz = (long)(int)_ctrl[trBase + 2] << 12;
			SetMac123(
				MacChainT(1, tx, m11 * vx, m12 * vy, m13 * vz),
				MacChainT(2, ty, m21 * vx, m22 * vy, m23 * vz),
				MacChainT(3, tz, m31 * vx, m32 * vy, m33 * vz),
				sf);
		}
		else
		{
			SetMac123(
				MacChain(1, m11 * vx, m12 * vy, m13 * vz),
				MacChain(2, m21 * vx, m22 * vy, m23 * vz),
				MacChain(3, m31 * vx, m32 * vy, m33 * vz),
				sf);
		}
	}

	// BK + LC * IR -> MAC1-3 -> IR1-3
	private void MultiplyColorMatrix(bool sf, bool lm)
	{
		int ir1 = (short)(_data[9] & 0xFFFF);
		int ir2 = (short)(_data[10] & 0xFFFF);
		int ir3 = (short)(_data[11] & 0xFFFF);

		long rbk = (long)(int)_ctrl[13] << 12;
		long gbk = (long)(int)_ctrl[14] << 12;
		long bbk = (long)(int)_ctrl[15] << 12;

		long lr1 = GetMatElem(16, 0), lr2 = GetMatElem(16, 1), lr3 = GetMatElem(16, 2);
		long lg1 = GetMatElem(16, 3), lg2 = GetMatElem(16, 4), lg3 = GetMatElem(16, 5);
		long lb1 = GetMatElem(16, 6), lb2 = GetMatElem(16, 7), lb3 = GetMatElem(16, 8);

		SetMac123(
			MacChainT(1, rbk, lr1 * ir1, lr2 * ir2, lr3 * ir3),
			MacChainT(2, gbk, lg1 * ir1, lg2 * ir2, lg3 * ir3),
			MacChainT(3, bbk, lb1 * ir1, lb2 * ir2, lb3 * ir3),
			sf);
		SetIr1((int)_mac1, lm);
		SetIr2((int)_mac2, lm);
		SetIr3((int)_mac3, lm);
	}

	// RGBC colour * IR -> depth-cue with FC
	private void DoCdp(int r, int g, int b, bool sf, bool lm)
	{
		// DPCS/DPCT: base colour in [1,27,16] is the 8-bit channel << 16, then the
		// standard TWO-STEP depth-cue toward FC (with intermediate IR saturation +
		// MAC truncation between the steps). The old fused single-step form skipped
		// that mid clamping and diverged once it saturated, ps1-tests gte test 201.
		InterpolateColor((long)r << 16, (long)g << 16, (long)b << 16, sf, lm);
		PushRgb((int)_mac1 >> 4, (int)_mac2 >> 4, (int)_mac3 >> 4, (int)(_data[6] >> 24));
	}

	/// <summary>
	/// Depth-cue interpolation:
	/// Step 1: no lm clamp, sets direction toward fog
	/// Step 2: lm clamp, blends object color with fog
	/// IR0=0 -> pure object color; IR0=4096 -> pure fog color (FC).
	/// Used by NCDS, NCDT, CDP, DPCS, INTPL.
	/// </summary>
	private void InterpolateColor(long inMac1, long inMac2, long inMac3, bool sf, bool lm)
	{
		long rfc = (long)(int)_ctrl[21];
		long gfc = (long)(int)_ctrl[22];
		long bfc = (long)(int)_ctrl[23];

		// Step 1: IR = (FC<<12 - inMac) >> shift, no lm clamp
		SetMac123((rfc << 12) - inMac1, (gfc << 12) - inMac2, (bfc << 12) - inMac3, sf);
		SetIr1((int)_mac1, false);
		SetIr2((int)_mac2, false);
		SetIr3((int)_mac3, false);

		// Step 2: MAC = (IR*IR0 + inMac) >> shift, with lm clamp
		int ir0 = (short)(_data[8] & 0xFFFF);
		int ir1 = (short)(_data[9] & 0xFFFF);
		int ir2 = (short)(_data[10] & 0xFFFF);
		int ir3 = (short)(_data[11] & 0xFFFF);
		SetMac123((long)ir1 * ir0 + inMac1, (long)ir2 * ir0 + inMac2, (long)ir3 * ir0 + inMac3, sf);
		SetIr1((int)_mac1, lm);
		SetIr2((int)_mac2, lm);
		SetIr3((int)_mac3, lm);
	}

	// MAC / IR / SZ / SXY saturators

	private const long Mac43BitMax = (1L << 43) - 1;
	private const long Mac43BitMin = -(1L << 43);

	// Raw (pre-sf-shift) MAC3 value, used by RTPS/RTPT to derive SZ3
	private long _rawMac3;

	private void SetMac123(long v1, long v2, long v3, bool sf)
	{
		// Check 44-bit overflow before shift
		if (v1 > Mac43BitMax) _ctrl[31] |= FlagMac1Pos;
		if (v1 < Mac43BitMin) _ctrl[31] |= FlagMac1Neg;
		if (v2 > Mac43BitMax) _ctrl[31] |= FlagMac2Pos;
		if (v2 < Mac43BitMin) _ctrl[31] |= FlagMac2Neg;
		if (v3 > Mac43BitMax) _ctrl[31] |= FlagMac3Pos;
		if (v3 < Mac43BitMin) _ctrl[31] |= FlagMac3Neg;

		_rawMac3 = v3; // store pre-shift value for SZ3 derivation

		if (sf)
		{
			v1 >>= 12;
			v2 >>= 12;
			v3 >>= 12;
		}

		// GTE MAC1-3 WRAP to 32 bits (the low bits of the shifted 44-bit
		// accumulator); they do NOT saturate. Overflow is recorded in FLAG above,
		// but the stored value truncates. The old Math.Clamp saturated to
		// INT_MIN/MAX, which the ps1-tests gte opcode suite flags immediately.
		_mac1 = (int)v1;
		_mac2 = (int)v2;
		_mac3 = (int)v3;

		_data[25] = (uint)(int)_mac1;
		_data[26] = (uint)(int)_mac2;
		_data[27] = (uint)(int)_mac3;
	}

	private void SetMac0(long val)
	{
		if (val > int.MaxValue) _ctrl[31] |= FlagMac0Pos;
		if (val < int.MinValue) _ctrl[31] |= FlagMac0Neg;
		_mac0 = (int)val; // MAC0 wraps to 32 bits (truncate); it does NOT saturate
		_data[24] = (uint)(int)_mac0;
	}

	private int SetIr1(int val, bool lm)
	{
		int lo = lm ? 0 : short.MinValue;
		int clamped = Math.Clamp(val, lo, short.MaxValue);
		if (clamped != val) _ctrl[31] |= FlagIr1Sat;
		_data[9] = (uint)(short)clamped;
		return clamped;
	}

	private int SetIr2(int val, bool lm)
	{
		int lo = lm ? 0 : short.MinValue;
		int clamped = Math.Clamp(val, lo, short.MaxValue);
		if (clamped != val) _ctrl[31] |= FlagIr2Sat;
		_data[10] = (uint)(short)clamped;
		return clamped;
	}

	private int SetIr3(int val, bool lm)
	{
		int lo = lm ? 0 : short.MinValue;
		int clamped = Math.Clamp(val, lo, short.MaxValue);
		if (clamped != val) _ctrl[31] |= FlagIr3Sat;
		_data[11] = (uint)(short)clamped;
		return clamped;
	}

	// RTPS/RTPT variant of SetIr3: the IR3 VALUE uses the sf-shifted MAC3 with the
	// lm lower limit, but the saturation FLAG is computed from the UNSHIFTED MAC3
	// (rawMac3 >> 12) against -8000h..+7FFFh, independent of lm (hardware quirk).
	private void SetIr3Rtp(int macShifted, long rawMac3, bool lm)
	{
		long check = rawMac3 >> 12;
		if (check < -0x8000 || check > 0x7FFF) _ctrl[31] |= FlagIr3Sat;
		int lo = lm ? 0 : short.MinValue;
		_data[11] = (uint)(short)Math.Clamp(macShifted, lo, short.MaxValue);
	}

	private void SetIr0(int val)
	{
		int clamped = Math.Clamp(val, 0, 0x1000);
		if (clamped != val) _ctrl[31] |= FlagIr0Sat;
		_data[8] = (uint)(short)clamped;
	}

	private void PushSZ(int val)
	{
		// FIFO: SZ0<-SZ1<-SZ2<-SZ3<-new
		_data[16] = _data[17];
		_data[17] = _data[18];
		_data[18] = _data[19];
		uint sz = (uint)Math.Clamp(val, 0, 0xFFFF);
		if (val < 0 || val > 0xFFFF) _ctrl[31] |= FlagSz3Sat;
		_data[19] = sz;
	}

	private int SetSx2(int val)
	{
		int clamped = Math.Clamp(val, -0x400, 0x3FF);
		if (clamped != val) _ctrl[31] |= FlagSx2Sat;
		return clamped;
	}

	private int SetSy2(int val)
	{
		int clamped = Math.Clamp(val, -0x400, 0x3FF);
		if (clamped != val) _ctrl[31] |= FlagSy2Sat;
		return clamped;
	}

	private void PushSXY(int sx, int sy)
	{
		_data[12] = _data[13];
		_data[13] = _data[14];
		_data[14] = (uint)((sx & 0xFFFF) | ((sy & 0xFFFF) << 16));
	}

	private void PushRgb(int r, int g, int b, int code)
	{
		_data[20] = _data[21];
		_data[21] = _data[22];
		int cr = Math.Clamp(r, 0, 0xFF); if (cr != r) _ctrl[31] |= FlagColRSat;
		int cg = Math.Clamp(g, 0, 0xFF); if (cg != g) _ctrl[31] |= FlagColGSat;
		int cb = Math.Clamp(b, 0, 0xFF); if (cb != b) _ctrl[31] |= FlagColBSat;
		_data[22] = (uint)(cr | (cg << 8) | (cb << 16) | ((code & 0xFF) << 24));
	}

	// Helpers

	private void GetVector(int idx, out short vx, out short vy, out short vz)
	{
		int base_ = idx * 2; // R0/2/4
		vx = (short)(_data[base_] & 0xFFFF);
		vy = (short)(_data[base_] >> 16);
		vz = (short)(_data[base_ + 1] & 0xFFFF);
	}

	// Get signed 16-bit matrix element. Elements stored two per control register.
	private long GetMatElem(int baseReg, int idx)
	{
		uint reg = _ctrl[baseReg + idx / 2];
		return (idx & 1) == 0 ? (short)(reg & 0xFFFF) : (short)(reg >> 16);
	}

	// UNR reciprocal divide, computes H/SZ with standard PSX overflow behaviour.
	private uint UNRDivide(uint h, uint sz)
	{
		if ((ulong)sz * 2 <= h)
		{
			_ctrl[31] |= FlagDivOver;
			return 0x1FFFF;
		}

		// Normalise both inputs so sz has bit 15 set (count 16-bit leading zeros).
		// LeadingZeroCount promotes ushort -> uint and counts 32-bit zeros, so subtract 16.
		int shift = sz == 0 ? 16 : System.Numerics.BitOperations.LeadingZeroCount((uint)(ushort)sz) - 16;
		uint lhs = h << shift;
		uint rhs = sz << shift;

		// Newton-Raphson reciprocal approximation (hardware-exact algorithm).
		uint divisor = rhs | 0x8000u;
		int x = (int)(0x101u + UnrTable[((divisor & 0x7FFFu) + 0x40u) >> 7]);
		int d = ((int)divisor * -x + 0x80) >> 8;
		uint recip = (uint)((x * (0x20000 + d) + 0x80) >> 8);

		ulong result = ((ulong)lhs * recip + 0x8000uL) >> 16;
		return (uint)Math.Min(result, 0x1FFFFuL);
	}

	private uint BuildOrgb()
	{
		int ir1 = Math.Clamp((int)(short)(_data[9] & 0xFFFF), 0, 0xF80) >> 7;
		int ir2 = Math.Clamp((int)(short)(_data[10] & 0xFFFF), 0, 0xF80) >> 7;
		int ir3 = Math.Clamp((int)(short)(_data[11] & 0xFFFF), 0, 0xF80) >> 7;
		return (uint)(ir1 | (ir2 << 5) | (ir3 << 10));
	}

	private static int LeadingZeroCount(int val)
	{
		if (val == 0) return 32;
		if (val < 0) return System.Numerics.BitOperations.LeadingZeroCount((uint)~val);
		return System.Numerics.BitOperations.LeadingZeroCount((uint)val);
	}
}
