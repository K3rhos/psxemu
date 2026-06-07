using System.Runtime.CompilerServices;

namespace PSXEmu;

/// <summary>
/// PSX Interrupt Controller - manages I_STAT (0x1F801070) and I_MASK (0x1F801074).
/// An IRQ fires the MIPS CPU interrupt line when (I_STAT & I_MASK) != 0.
/// </summary>
public class PsxInterruptController
{
	private readonly Psx _psx;

	/// <summary>Interrupt status register (I_STAT). Bit set = interrupt occurred.</summary>
	public uint IStat { get; private set; }

	/// <summary>Interrupt mask register (I_MASK). Bit set = interrupt enabled.</summary>
	public uint IMask { get; private set; }

	public PsxInterruptController(Psx psx) => _psx = psx;

	// FMV-DIAG: track raise (0->1) / ack (1->0) per IRQ bit so we can see whether
	// IRQs are being collapsed (raise count >> ack count) due to slow CPU/ISR.
	// Indexed by IRQ bit number (0..10).
	public readonly long[] DiagRaiseCount = new long[11];
	public readonly long[] DiagAckCount = new long[11];

	public void Reset()
	{
		IStat = 0;
		IMask = 0;
		Array.Clear(DiagRaiseCount);
		Array.Clear(DiagAckCount);
	}

	/// <summary>Raise an interrupt by setting its bit in I_STAT.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Raise(uint irqBit)
	{
		// Count only the rising edge, successive raises while a bit is already
		// set don't generate new IRQs (PSX I_STAT is a single bit per source).
		uint before = IStat;
		IStat |= irqBit;
		uint newlySet = (~before) & IStat;
		if (newlySet != 0)
		{
			for (int b = 0; b < DiagRaiseCount.Length; b++)
				if ((newlySet & (1u << b)) != 0) DiagRaiseCount[b]++;
		}
	}

	/// <summary>
	/// Programmatically clear an interrupt bit in I_STAT.
	/// Used for level-triggered IRQs (DMA) whose line follows a hardware register bit (DICR bit 31).
	/// When that bit deasserts, the CPU interrupt line must also deassert immediately.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Clear(uint irqBit) => IStat &= ~irqBit;

	/// <summary>Returns true if any enabled interrupt is pending (IRQ line to CPU is asserted).</summary>
	public bool IrqPending => (IStat & IMask) != 0;

	public uint ReadWord(uint offset)
	{
		return offset switch
		{
			0 => IStat,
			4 => IMask,
			_ => 0,
		};
	}

	public void WriteWord(uint offset, uint value)
	{
		switch (offset)
		{
			case 0: // I_STAT: writing 0 bits clears those interrupt flags (acknowledge)
				{
					uint before = IStat;
					IStat &= value;
					uint cleared = before & ~IStat;
					if (cleared != 0)
					{
						for (int b = 0; b < DiagAckCount.Length; b++)
							if ((cleared & (1u << b)) != 0) DiagAckCount[b]++;
					}
				}
				break;
			case 4: // I_MASK
				IMask = value & 0x7FF;
				break;
		}
	}

	public ushort ReadHalf(uint offset)
	{
		return (ushort)ReadWord(offset & ~1u);
	}

	public void WriteHalf(uint offset, ushort value)
	{
		WriteWord(offset & ~1u, value);
	}

	// Diag* arrays are debug counters, not machine state, intentionally skipped.
	public void SaveState(StateWriter w)
	{
		w.U32(IStat);
		w.U32(IMask);
	}

	public void LoadState(StateReader r)
	{
		IStat = r.U32();
		IMask = r.U32();
	}
}
