using System.Runtime.CompilerServices;

namespace PSXEmu;

/// <summary>
/// PSX memory bus. Translates virtual addresses to physical regions and
/// dispatches reads/writes to the appropriate hardware.
///
/// Virtual address masking (strips KSEG0/KSEG1 prefix):
///   KUSEG  0x00000000-0x7FFFFFFF -> pass through
///   KSEG0  0x80000000-0x9FFFFFFF -> physical (& 0x1FFFFFFF)
///   KSEG1  0xA0000000-0xBFFFFFFF -> physical (& 0x1FFFFFFF)
///
/// Physical address map:
///   0x00000000-0x001FFFFF  Main RAM (2 MB, mirrored *4 in 0-7FFFFF)
///   0x1F000000-0x1F7FFFFF  Expansion 1
///   0x1F800000-0x1F8003FF  Scratchpad (1 KB)
///   0x1F801000-0x1F803FFF  Hardware I/O
///   0x1FC00000-0x1FC7FFFF  BIOS ROM (512 KB)
/// </summary>
public class PsxMemory
{
	private readonly Psx _psx;

	public byte[] Ram { get; } = new byte[PsxConstants.RamSize];
	public byte[] Bios { get; } = new byte[PsxConstants.BiosSize];
	public byte[] Scratchpad { get; } = new byte[PsxConstants.ScratchpadSize];

	// Cache isolation flag (when set, writes go to cache, not RAM)
	private bool _cacheIsolated;

	// --- WATCHPOINT (diagnostic) ---
	// Two zones we can watch simultaneously:
	//   Zone A: Driver 2 StCdInterrupt bail reason code (DAT_80128adc).
	//           Values 1-10 indicate which path StCdInterrupt took. If we
	//           see a non-10 value repeating during the FMV stall, that's
	//           the bail-out preventing further DMA3 sector loads. See
	//           StCdInterrupt in FMV.EXE (Ghidra) for the meaning of each.
	//   Zone B: not in use
	// Each write that overlaps either zone gets logged with PC/RA/cycle.
	// Set bounds to 0 to disable that zone.
	public uint WatchAPhys = 0x00128adc;
	public uint WatchBStart = 0;
	public uint WatchBEnd   = 0;
	private int _watchpointHits;
	private const int WatchpointMaxLogs = 500;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void CheckWatchpoint(uint phys, uint value, int width)
	{
		bool hitA = WatchAPhys != 0 && (phys & ~3u) == (WatchAPhys & ~3u);
		bool hitB = WatchBStart != 0 && phys >= WatchBStart && phys < WatchBEnd
		         && ((phys - WatchBStart) & 31u) < 4;
		if (!hitA && !hitB) return;
		if (_watchpointHits >= WatchpointMaxLogs) return;
		_watchpointHits++;
		string zone = hitA ? "stcd_reason" : $"slot[{(int)((phys - WatchBStart) / 32)}].+{(phys - WatchBStart) % 32:X2}";
		uint pc  = _psx.Cpu.Pc;
		uint ra  = _psx.Cpu.Gpr[31];
		long cyc = _psx.Cpu.Cycles;
		PsxLog.Write(PsxLogCategory.Memory, PsxLogLevel.Warn,
			$"[WATCH #{_watchpointHits}] cycle={cyc} write{width} {zone} @0x{phys | 0x80000000u:X8}=0x{value:X8} PC=0x{pc:X8} RA=0x{ra:X8}");
	}

	public PsxMemory(Psx psx) => _psx = psx;

	public void Reset()
	{
		Array.Clear(Ram);
		Array.Clear(Scratchpad);
		_cacheIsolated = false;
	}

	public void LoadBios(byte[] data)
	{
		int len = Math.Min(data.Length, PsxConstants.BiosSize);
		Array.Copy(data, 0, Bios, 0, len);
	}

	// Called when SR.Isc changes
	public void SetCacheIsolated(bool isolated) => _cacheIsolated = isolated;

	// Save-state: RAM + scratchpad + cache-isolation flag. BIOS is constant ROM
	// (reloaded on boot, not dynamic state); watchpoints are debug-only.
	public void SaveState(StateWriter w)
	{
		w.Bytes(Ram);
		w.Bytes(Scratchpad);
		w.Bool(_cacheIsolated);
	}

	public void LoadState(StateReader r)
	{
		r.Bytes(Ram);
		r.Bytes(Scratchpad);
		_cacheIsolated = r.Bool();
	}

	// --- Address translation ---

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static uint ToPhysical(uint vaddr)
	{
		// Strip top 3 bits to map KSEG0/KSEG1/KUSEG all to physical space
		return vaddr & 0x1FFFFFFFu;
	}

	// --- Public read API ---

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public uint ReadWord(uint addr)
	{
		uint phys = ToPhysical(addr);

		if (phys < 0x00800000) // RAM (2MB * 4 mirrors)
		{
			uint off = phys & (PsxConstants.RamSize - 1);
			return ReadU32(Ram, off);
		}
		if (phys >= 0x1F800000 && phys < 0x1F800400) // Scratchpad
			return ReadU32(Scratchpad, phys - 0x1F800000);
		if (phys >= 0x1F801000 && phys < 0x1F804000) // I/O
			return ReadIoWord(phys);
		if (phys >= 0x1FC00000 && phys < 0x1FC80000) // BIOS
			return ReadU32(Bios, phys - 0x1FC00000);

		PsxLog.Write(PsxLogCategory.Memory, PsxLogLevel.Debug, $"ReadWord unmapped: 0x{addr:X8}");
		return 0xFFFFFFFF;
	}

	/// <summary>
	/// Per-region per-size CPU bus-cycle cost for a READ access.
	/// Charged to <see cref="MipsCore.Cycles"/> by the load handlers.
	///
	/// Size-aware regions (BIOS/SPU/CDROM/EXP1) charge different cycles for
	/// byte vs halfword vs word access, real hardware bus widths differ.
	/// Fixed regions (RAM/Scratchpad/IO) return the same cost regardless of
	/// size. See <see cref="PsxConstants"/> for the values + their derivation
	/// from MEMCTRL register defaults.
	///
	/// DMA code does NOT charge via this, it has its own per-block tick
	/// accounting (N + ceil(N/16)) in <see cref="PsxDmaController.ChargeBlockCycles"/>.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int GetReadCycles(uint addr, PsxConstants.BusAccessSize size)
	{
		uint phys = ToPhysical(addr);
		if (phys < 0x00800000) return PsxConstants.BusCyclesRam;
		if (phys >= 0x1F800000 && phys < 0x1F800400) return PsxConstants.BusCyclesScratchpad;
		if (phys >= 0x1F801C00 && phys < 0x1F802000) return size switch
		{
			PsxConstants.BusAccessSize.Byte => PsxConstants.BusCyclesSpuByte,
			PsxConstants.BusAccessSize.Half => PsxConstants.BusCyclesSpuHalf,
			_                               => PsxConstants.BusCyclesSpuWord,
		};
		if (phys >= 0x1F801800 && phys < 0x1F801804) return size switch
		{
			PsxConstants.BusAccessSize.Byte => PsxConstants.BusCyclesCdromByte,
			PsxConstants.BusAccessSize.Half => PsxConstants.BusCyclesCdromHalf,
			_                               => PsxConstants.BusCyclesCdromWord,
		};
		if (phys >= 0x1F801000 && phys < 0x1F804000) return PsxConstants.BusCyclesIo;
		if (phys >= 0x1FC00000 && phys < 0x1FC80000) return size switch
		{
			PsxConstants.BusAccessSize.Byte => PsxConstants.BusCyclesBiosByte,
			PsxConstants.BusAccessSize.Half => PsxConstants.BusCyclesBiosHalf,
			_                               => PsxConstants.BusCyclesBiosWord,
		};
		if (phys >= 0x1F000000 && phys < 0x1F800000) return size switch
		{
			PsxConstants.BusAccessSize.Byte => PsxConstants.BusCyclesExp1Byte,
			PsxConstants.BusAccessSize.Half => PsxConstants.BusCyclesExp1Half,
			_                               => PsxConstants.BusCyclesExp1Word,
		};
		return PsxConstants.BusCyclesUnmapped;
	}

	/// <summary>
	/// Legacy no-size overload, returns word-sized cost. Used by callers
	/// that don't have access-size info (e.g., instruction-fetch icache fill,
	/// which is always word-sized).
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int GetReadCycles(uint addr) => GetReadCycles(addr, PsxConstants.BusAccessSize.Word);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public ushort ReadHalf(uint addr)
	{
		uint phys = ToPhysical(addr);

		if (phys < 0x00800000)
			return ReadU16(Ram, phys & (PsxConstants.RamSize - 1));
		if (phys >= 0x1F800000 && phys < 0x1F800400)
			return ReadU16(Scratchpad, phys - 0x1F800000);
		if (phys >= 0x1F801000 && phys < 0x1F804000)
			return ReadIoHalf(phys);
		if (phys >= 0x1FC00000 && phys < 0x1FC80000)
			return ReadU16(Bios, phys - 0x1FC00000);

		return 0xFFFF;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public byte ReadByte(uint addr)
	{
		uint phys = ToPhysical(addr);

		if (phys < 0x00800000)
			return Ram[phys & (PsxConstants.RamSize - 1)];
		if (phys >= 0x1F800000 && phys < 0x1F800400)
			return Scratchpad[phys - 0x1F800000];
		if (phys >= 0x1F801000 && phys < 0x1F804000)
			return ReadIoByte(phys);
		if (phys >= 0x1FC00000 && phys < 0x1FC80000)
			return Bios[phys - 0x1FC00000];
		if (phys >= 0x1F000000 && phys < 0x1F800000)
			return 0xFF; // Expansion 1 (open bus)

		return 0xFF;
	}

	// --- Public write API ---

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void WriteWord(uint addr, uint value)
	{
		uint phys = ToPhysical(addr);

		if (phys < 0x00800000)
		{
			uint off = phys & (PsxConstants.RamSize - 1);
			// Cache isolation: CPU writes go to d-cache, not RAM.
			// Invalidate the matching i-cache line, BIOS uses
			// SR.IsC + RAM-address writes on cold boot to flush the i-cache
			// before loading kernel code into it. Without invalidation the
			// stale tag survives and the freshly-loaded code is never fetched.
			if (_cacheIsolated)
			{
				_psx.Cpu.InvalidateICacheLine(addr);
				return;
			}
			CheckWatchpoint(phys, value, 32);
			WriteU32(Ram, off, value);
			return;
		}
		if (phys >= 0x1F800000 && phys < 0x1F800400)
		{
			if (_cacheIsolated) return;
			WriteU32(Scratchpad, phys - 0x1F800000, value);
			return;
		}
		if (phys >= 0x1F801000 && phys < 0x1F804000)
		{
			WriteIoWord(phys, value);
			return;
		}
		if (phys >= 0x1FC00000 && phys < 0x1FC80000)
			return; // BIOS is ROM

		if (phys >= 0x1FFE0000) return; // Cache control registers (harmless, no log)
		PsxLog.Write(PsxLogCategory.Memory, PsxLogLevel.Debug, $"WriteWord unmapped: 0x{addr:X8} = 0x{value:X8}");
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void WriteHalf(uint addr, ushort value) => WriteHalf(addr, value, value);

	/// <summary><paramref name="cpuWord"/> carries the FULL 32-bit source register
	/// from a CPU store, so I/O registers that ignore byte-enables (DMA) can latch
	/// the whole data-bus word on a sub-word write. RAM/scratchpad use the masked
	/// <paramref name="value"/>.</summary>
	public void WriteHalf(uint addr, ushort value, uint cpuWord)
	{
		uint phys = ToPhysical(addr);

		if (phys < 0x00800000)
		{
			// See WriteWord, invalidate i-cache line on isolated writes.
			if (_cacheIsolated)
			{
				_psx.Cpu.InvalidateICacheLine(addr);
				return;
			}
			uint off = phys & (PsxConstants.RamSize - 1);
			CheckWatchpoint(phys, value, 16);
			WriteU16(Ram, off, value);
			return;
		}
		if (phys >= 0x1F800000 && phys < 0x1F800400)
		{
			if (_cacheIsolated) return;
			WriteU16(Scratchpad, phys - 0x1F800000, value);
			return;
		}
		if (phys >= 0x1F801000 && phys < 0x1F804000)
		{
			WriteIoHalf(phys, value, cpuWord);
			return;
		}
	}

	public void WriteByte(uint addr, byte value) => WriteByte(addr, value, value);

	/// <summary>See <see cref="WriteHalf(uint,ushort,uint)"/>, <paramref name="cpuWord"/>
	/// is the full CPU source register for the I/O byte-enable quirk.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void WriteByte(uint addr, byte value, uint cpuWord)
	{
		uint phys = ToPhysical(addr);

		if (phys < 0x00800000)
		{
			// See WriteWord, invalidate i-cache line on isolated writes.
			if (_cacheIsolated)
			{
				_psx.Cpu.InvalidateICacheLine(addr);
				return;
			}
			CheckWatchpoint(phys, value, 8);
			Ram[phys & (PsxConstants.RamSize - 1)] = value;
			return;
		}
		if (phys >= 0x1F800000 && phys < 0x1F800400)
		{
			if (_cacheIsolated) return;
			Scratchpad[phys - 0x1F800000] = value;
			return;
		}
		if (phys >= 0x1F801000 && phys < 0x1F804000)
		{
			WriteIoByte(phys, value, cpuWord);
			return;
		}
	}


	// --- I/O dispatch ---

	// Diagnostic: log each unique I/O read address once (capped at 256 entries).
	private readonly System.Collections.Generic.HashSet<uint> _seenIoReads = new();
	private void LogIoRead(uint addr, string width, uint value)
	{
		if (_seenIoReads.Count >= 256) return;
		if (_seenIoReads.Add(addr))
			PsxLog.Write(PsxLogCategory.IO, PsxLogLevel.Info,
				$"[DIAG] IO {width} read 0x{addr:X8} => 0x{value:X8}");
	}

	/// <summary>
	/// Byte read from I/O space. Dispatches devices that have per-byte FIFO side-effects
	/// (CDROM, controller) directly; falls through to word-read + extract for everything else.
	/// </summary>
	private byte ReadIoByte(uint addr)
	{
		byte result;
		// CDROM: byte-addressed FIFOs, must not go through ReadWord which has side effects
		if (addr >= 0x1F801800 && addr < 0x1F801810)
			result = _psx.Cdrom.ReadByte(addr - 0x1F801800);
		else
		{
			// All other I/O: read the enclosing word, extract the byte
			uint word = ReadIoWord(addr & ~3u);
			int shift = (int)(addr & 3) * 8;
			result = (byte)(word >> shift);
		}
		LogIoRead(addr, "byte", result);
		return result;
	}

	/// <summary>
	/// Halfword read from I/O space. Dispatches SPU (16-bit registers) directly;
	/// falls through to word-read + extract for everything else.
	/// </summary>
	private ushort ReadIoHalf(uint addr)
	{
		ushort result;
		// SPU: 16-bit registers, read the aligned half directly
		if (addr >= 0x1F801C00 && addr < 0x1F802000)
			result = _psx.Spu.ReadHalf(addr - 0x1F801C00);
		// Controller/pad: has 16-bit registers at odd offsets (CTRL=0x0A, BAUD=0x0E)
		// that don't align to 32-bit boundaries, so read directly.
		else if (addr >= 0x1F801040 && addr < 0x1F801060)
			result = _psx.Controller.ReadHalf(addr - 0x1F801040);
		// CDROM: byte-streamed FIFOs. A halfword read must consume exactly two
		// bytes from the FIFO, not four, falling through to ReadIoWord would
		// read four bytes (Cdrom.ReadWord internally does 4 x ReadByte) and
		// drain the data/response FIFO twice as fast as real hardware.
		else if (addr >= 0x1F801800 && addr < 0x1F801810)
		{
			byte lo = _psx.Cdrom.ReadByte(addr - 0x1F801800);
			byte hi = _psx.Cdrom.ReadByte((addr + 1) - 0x1F801800);
			result = (ushort)(lo | (hi << 8));
		}
		else
		{
			// All other I/O: read the enclosing word, extract the halfword
			uint word = ReadIoWord(addr & ~3u);
			int shift = (int)(addr & 2) * 8;
			result = (ushort)(word >> shift);
		}
		LogIoRead(addr, "half", result);
		return result;
	}

	private uint ReadIoWord(uint addr)
	{
		uint _ioResult = ReadIoWordInner(addr);
		LogIoRead(addr, "word", _ioResult);
		return _ioResult;
	}

	private uint ReadIoWordInner(uint addr)
	{
		// Interrupt controller
		if (addr >= 0x1F801070 && addr < 0x1F801078)
			return _psx.Interrupts.ReadWord(addr - 0x1F801070);

		// DMA
		if (addr >= 0x1F801080 && addr < 0x1F801100)
			return _psx.Dma.ReadWord(addr);
		if (addr == 0x1F8010F0 || addr == 0x1F8010F4)
			return _psx.Dma.ReadWord(addr);

		// Timers
		if (addr >= 0x1F801100 && addr < 0x1F801130)
			return _psx.Timers.ReadWord(addr - 0x1F801100);

		// CDROM (4 byte registers at 0x1F801800)
		if (addr >= 0x1F801800 && addr < 0x1F801810)
			return _psx.Cdrom.ReadWord(addr - 0x1F801800);

		// MDEC
		// 0x1F801820 = MDEC0: data register (output FIFO read)
		// 0x1F801824 = MDEC1: status register
		if (addr == 0x1F801820) return _psx.Mdec.ReadWord(0);
		if (addr == 0x1F801824) return _psx.Mdec.ReadWord(4);

		// GPU
		if (addr == 0x1F801810) return _psx.Gpu.ReadGpuData();
		if (addr == 0x1F801814) return _psx.Gpu.ReadGpuStat();

		// Controller/SIO0
		if (addr >= 0x1F801040 && addr < 0x1F801060)
			return _psx.Controller.ReadWord(addr - 0x1F801040);

		// SPU
		if (addr >= 0x1F801C00 && addr < 0x1F802000)
			return _psx.Spu.ReadWord(addr - 0x1F801C00);

		// RAM size register
		if (addr == 0x1F801060) return 0x00000B88;

		// Memory control registers (return plausible values)
		if (addr >= 0x1F801000 && addr < 0x1F801024) return 0;

		// Post boot status
		if (addr == 0x1F802041) return 0;

		// Expansion Region 2 (0x1F802000-0x1F803FFF): almost entirely unmapped,
		// reads return OPEN BUS (all-ones) on real hardware, not zero. (The POST
		// register at 0x1F802041 is handled above.)  Sized reads mask this down
		// to 0xffff / 0xff naturally. ps1-tests cpu/io-access-bitwidth; matches
		// real hardware.
		if (addr >= 0x1F802000 && addr < 0x1F804000) return 0xFFFFFFFFu;

		PsxLog.Write(PsxLogCategory.IO, PsxLogLevel.Debug, $"ReadIO unmapped: 0x{addr:X8}");
		return 0;
	}

	private void WriteIoWord(uint addr, uint value)
	{
		if (addr >= 0x1F801070 && addr < 0x1F801078) { _psx.Interrupts.WriteWord(addr - 0x1F801070, value); return; }
		if (addr >= 0x1F801080 && addr < 0x1F801100) { _psx.Dma.WriteWord(addr, value); return; }
		if (addr == 0x1F8010F0 || addr == 0x1F8010F4) { _psx.Dma.WriteWord(addr, value); return; }
		if (addr >= 0x1F801100 && addr < 0x1F801130) { _psx.Timers.WriteWord(addr - 0x1F801100, value); return; }
		if (addr >= 0x1F801800 && addr < 0x1F801810) { _psx.Cdrom.WriteWord(addr - 0x1F801800, value); return; }
		if (addr == 0x1F801820) { _psx.Mdec.WriteWord(0, value); return; }
		if (addr == 0x1F801824) { _psx.Mdec.WriteWord(4, value); return; }
		if (addr == 0x1F801810) { _psx.Gpu.WriteGp0(value); return; }
		if (addr == 0x1F801814) { _psx.Gpu.WriteGp1(value); return; }
		if (addr >= 0x1F801040 && addr < 0x1F801060) { _psx.Controller.WriteWord(addr - 0x1F801040, value); return; }
		if (addr >= 0x1F801C00 && addr < 0x1F802000) { _psx.Spu.WriteWord(addr - 0x1F801C00, value); return; }

		// Silently ignore known-harmless write-only registers:
		// 0x1F801000-0x1F80101C: Memory control (expansion base/delays)
		// 0x1F801060: RAM size
		// 0x1F802000-0x1F8020FF: Expansion 2 (POST/debug LEDs)
		if (addr >= 0x1F801000 && addr < 0x1F801024) return; // memory control
		if (addr == 0x1F801060) return;                       // RAM size
		if (addr >= 0x1F802000 && addr < 0x1F802100) return; // expansion 2 POST
	}

	private void WriteIoHalf(uint addr, ushort value, uint cpuWord)
	{
		if (addr >= 0x1F801070 && addr < 0x1F801078) { _psx.Interrupts.WriteHalf(addr - 0x1F801070, value); return; }
		if (addr >= 0x1F801100 && addr < 0x1F801130) { _psx.Timers.WriteHalf(addr - 0x1F801100, value); return; }
		if (addr >= 0x1F801040 && addr < 0x1F801060) { _psx.Controller.WriteHalf(addr - 0x1F801040, value); return; }
		if (addr >= 0x1F801080 && addr < 0x1F801100)
		{
			// DICR (0x1F8010F4) carries per-channel IRQ-enable/flag BYTES that
			// libcd's STR/FMV streaming toggles with sub-word RMW, it clears
			// channel-3's enable byte for non-final chunks and re-enables it for
			// the last, using that as the entire "frame ready" gate. Those writes
			// MUST be byte/half POSITIONED (a real RMW at the addressed offset), so
			// route them through the sized DMA write path. The full-word latch
			// below would put the source GPR's low byte into the wrong DICR field
			// and defeat the per-chunk gating (frame consumed after ~2 chunks).
			if (addr >= 0x1F8010F4 && addr < 0x1F8010F8) { _psx.Dma.WriteHalf(addr, value); return; }
			// Real PSX ignores byte-enables on I/O writes: a sub-word store to a
			// 32-bit DMA register latches the FULL data-bus word (the source
			// GPR), not just the addressed half/byte. e.g. `sh 0x12345678` to
			// DMA0_ADDR reads back 0x345678, not 0x5678, a game poking MADR /
			// BCR / CHCR / DPCR with SH/SB would otherwise get a corrupted
			// transfer address or control word. (ps1-tests cpu/io-access-bitwidth;
			// matches real hardware.)  The 32-bit DMA write path already handles
			// DICR's write-1-to-clear ack bits, and acking on the full word is correct,
			// on HW the SH/SB drive the whole GPR onto the bus.
			_psx.Dma.WriteWord(addr & ~3u, cpuWord);
			return;
		}
		if (addr >= 0x1F801C00 && addr < 0x1F802000) { _psx.Spu.WriteHalf(addr - 0x1F801C00, value); return; }

		// CDROM registers (0x1F801800-0x1F80180F): byte-mapped on the 8-bit CDROM
		// bus. A halfword write splits into two sequential byte writes, that's
		// what real HW's bus arbiter does, and our `Cdrom.WriteByte` is the
		// canonical entry point (`Cdrom.WriteWord` already does the same split
		// internally for the 4-byte case). Without this, the previous
		// fallthrough to `WriteIoWord(addr & ~3u, value)` zero-extended the
		// halfword and clobbered offsets +2/+3 with zeros, silently breaking
		// CDROM register state any time a game used SH.
		if (addr >= 0x1F801800 && addr < 0x1F801810)
		{
			uint off = addr - 0x1F801800;
			_psx.Cdrom.WriteByte(off, (byte)value);
			_psx.Cdrom.WriteByte(off + 1, (byte)(value >> 8));
			return;
		}

		// GPU GP0 (0x1F801810) / GP1 (0x1F801814): 32-bit write-only
		// command/control ports. SH is uncommon (most games use SW) but legal;
		// shift the halfword into its proper slot of a 32-bit value (the other
		// half stays zero). Without the shift, a SH at 0x1F801812
		// (high half of GP0) would have been routed to GP0's LOW slot, doubly
		// wrong: wrong register slot AND zeros in the high half.
		if (addr >= 0x1F801810 && addr < 0x1F801818)
		{
			int shift = (int)((addr & 2u) * 8u);
			uint shifted = (uint)value << shift;
			if ((addr & ~3u) == 0x1F801810) _psx.Gpu.WriteGp0(shifted);
			else _psx.Gpu.WriteGp1(shifted);
			return;
		}

		// MDEC0 (0x1F801820) / MDEC1 (0x1F801824): same fixup as GPU. MDEC0 is
		// the command/data FIFO push port (the WriteWord handler enqueues both
		// halves into the input FIFO, preserving slot ordering matters here),
		// MDEC1 is the control register (reset / DMAin/out enables in the high
		// bits). Without the shift, SH at 0x1F801826 would have written the
		// halfword to MDEC1 bits [15:0] instead of [31:16], silently failing to
		// toggle DMA enables.
		if (addr >= 0x1F801820 && addr < 0x1F801828)
		{
			int shift = (int)((addr & 2u) * 8u);
			uint shifted = (uint)value << shift;
			_psx.Mdec.WriteWord((addr & ~3u) - 0x1F801820, shifted);
			return;
		}

		// Fallthrough, memory control (0x1F801000-0x1F801023), RAM size
		// (0x1F801060), expansion 2 POST (0x1F802000-0x1F8020FF) and any
		// unmapped address. All of those silently ignore writes regardless of
		// width, so the value passed here doesn't matter. Kept for forward
		// compatibility with new word handlers that don't yet have an explicit
		// halfword path.
		WriteIoWord(addr & ~3u, value);
	}

	private void WriteIoByte(uint addr, byte value, uint cpuWord)
	{
		// CDROM: byte-addressed registers, native byte handler.
		if (addr >= 0x1F801800 && addr < 0x1F801810)
		{
			_psx.Cdrom.WriteByte(addr - 0x1F801800, value);
			return;
		}

		// DICR (0x1F8010F4): byte-positioned RMW (see WriteIoHalf). libcd's
		// STR/FMV streaming toggles individual IRQ-enable BYTES of DICR per chunk;
		// the full-word delegation below would smear the byte across the wrong
		// DICR field and break the per-chunk frame-ready gating. Route straight to
		// the sized byte write so exactly the addressed byte is updated.
		if (addr >= 0x1F8010F4 && addr < 0x1F8010F8)
		{
			_psx.Dma.WriteByte(addr, value);
			return;
		}

		// Every other I/O register is >= 16-bit and ignores byte-enables: a sub-
		// word store latches the FULL data-bus word (the source GPR) into the
		// addressed register, masked to that register's own width, i.e. an SB
		// behaves exactly like an SH/SW of the same source register. So just
		// delegate to the halfword path, which already routes each peripheral to
		// its native-width write (and the DMA range to a full 32-bit write via
		// cpuWord). Real PSX byte-enables are ignored on I/O, so an `sb` to
		// JOY_MODE / T0_TARGET / I_MASK / SPUCNT must latch the low 16 bits, not
		// just the low byte. (ps1-tests cpu/io-access-bitwidth; matches real hardware.)
		// This also drops the old byte-RMW: with the full-word model there's nothing
		// to read-merge, the register simply latches cpuWord, so the side-effect-laden
		// reads (Controller RX FIFO, I_STAT ack) are gone too.
		WriteIoHalf(addr & ~1u, (ushort)cpuWord, cpuWord);
	}

	// --- Raw memory helpers (little-endian) ---

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static uint ReadU32(byte[] mem, uint off)
	{
		if (off + 3 >= mem.Length) return 0;
		return mem[off] | ((uint)mem[off + 1] << 8) | ((uint)mem[off + 2] << 16) | ((uint)mem[off + 3] << 24);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static ushort ReadU16(byte[] mem, uint off)
	{
		if (off + 1 >= mem.Length) return 0;
		return (ushort)(mem[off] | (mem[off + 1] << 8));
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void WriteU32(byte[] mem, uint off, uint val)
	{
		if (off + 3 >= mem.Length) return;
		mem[off] = (byte)val;
		mem[off + 1] = (byte)(val >> 8);
		mem[off + 2] = (byte)(val >> 16);
		mem[off + 3] = (byte)(val >> 24);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void WriteU16(byte[] mem, uint off, ushort val)
	{
		if (off + 1 >= mem.Length) return;
		mem[off] = (byte)val;
		mem[off + 1] = (byte)(val >> 8);
	}
}
