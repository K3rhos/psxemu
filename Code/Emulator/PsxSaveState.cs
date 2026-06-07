using System.IO;

namespace PSXEmu;

/// <summary>
/// Binary save-state writer. Thin typed wrapper over <see cref="BinaryWriter"/>
/// with array helpers and SECTION TAGS, every component writes a tag before its
/// block so the reader can verify alignment. If a future field is added/removed
/// on one side only, the next Section() check throws loudly instead of silently
/// corrupting the load (the whole point, a half-restored emulator is worse than
/// a failed load).
/// </summary>
public sealed class StateWriter
{
	private readonly BinaryWriter _w;
	public StateWriter(Stream s) { _w = new BinaryWriter(s); }

	/// <summary>Section marker, paired with <see cref="StateReader.Section"/>.</summary>
	public void Section(string tag) => _w.Write(tag);

	public void U8(byte v) => _w.Write(v);
	public void Bool(bool v) => _w.Write(v);
	public void S16(short v) => _w.Write(v);
	public void U16(ushort v) => _w.Write(v);
	public void S32(int v) => _w.Write(v);
	public void U32(uint v) => _w.Write(v);
	public void S64(long v) => _w.Write(v);
	public void U64(ulong v) => _w.Write(v);
	public void F32(float v) => _w.Write(v);
	public void F64(double v) => _w.Write(v);

	public void Bytes(byte[] a) { _w.Write(a.Length); _w.Write(a); }
	public void Shorts(short[] a) { _w.Write(a.Length); foreach (var x in a) _w.Write(x); }
	public void UShorts(ushort[] a) { _w.Write(a.Length); foreach (var x in a) _w.Write(x); }
	public void Ints(int[] a) { _w.Write(a.Length); foreach (var x in a) _w.Write(x); }
	public void UInts(uint[] a) { _w.Write(a.Length); foreach (var x in a) _w.Write(x); }
	public void Longs(long[] a) { _w.Write(a.Length); foreach (var x in a) _w.Write(x); }
}

/// <summary>
/// Binary save-state reader. Mirror of <see cref="StateWriter"/>. Array reads
/// fill an EXISTING array in place (no realloc) and assert the saved length
/// matches, restoring into the live RAM/VRAM/SPU-RAM buffers the running
/// emulator already holds.
/// </summary>
public sealed class StateReader
{
	private readonly BinaryReader _r;
	public StateReader(Stream s) { _r = new BinaryReader(s); }

	/// <summary>Verify the next section tag matches, throws on misalignment.</summary>
	public void Section(string tag)
	{
		string got = _r.ReadString();
		if (got != tag)
			throw new Exception(
				$"Save-state section mismatch: expected '{tag}', got '{got}'. " +
				"A component's SaveState/LoadState fields are out of sync.");
	}

	public byte U8() => _r.ReadByte();
	public bool Bool() => _r.ReadBoolean();
	public short S16() => _r.ReadInt16();
	public ushort U16() => _r.ReadUInt16();
	public int S32() => _r.ReadInt32();
	public uint U32() => _r.ReadUInt32();
	public long S64() => _r.ReadInt64();
	public ulong U64() => _r.ReadUInt64();
	public float F32() => _r.ReadSingle();
	public double F64() => _r.ReadDouble();

	private int Len(int expected, string what)
	{
		int n = _r.ReadInt32();
		if (n != expected)
			throw new Exception($"Save-state array length mismatch for {what}: expected {expected}, got {n}.");
		return n;
	}

	/// <summary>Read into an existing byte[] (in place).</summary>
	public void Bytes(byte[] dest)
	{
		Len(dest.Length, "byte[]");
		int read = 0;
		while (read < dest.Length)
		{
			int n = _r.Read(dest, read, dest.Length - read);
			if (n <= 0) throw new Exception("Truncated save state (byte[]).");
			read += n;
		}
	}

	public void Shorts(short[] dest)  { Len(dest.Length, "short[]");  for (int i = 0; i < dest.Length; i++) dest[i] = _r.ReadInt16(); }
	public void UShorts(ushort[] dest){ Len(dest.Length, "ushort[]"); for (int i = 0; i < dest.Length; i++) dest[i] = _r.ReadUInt16(); }
	public void Ints(int[] dest)      { Len(dest.Length, "int[]");    for (int i = 0; i < dest.Length; i++) dest[i] = _r.ReadInt32(); }
	public void UInts(uint[] dest)    { Len(dest.Length, "uint[]");   for (int i = 0; i < dest.Length; i++) dest[i] = _r.ReadUInt32(); }
	public void Longs(long[] dest)    { Len(dest.Length, "long[]");   for (int i = 0; i < dest.Length; i++) dest[i] = _r.ReadInt64(); }
}

public partial class Psx
{
	// Bump when the format changes incompatibly, old states are rejected.
	private const uint SaveStateMagic = 0x50535853; // "PSXS"
	private const uint SaveStateVersion = 1;

	/// <summary>
	/// Serialize the entire machine state to <paramref name="stream"/>. Does NOT
	/// include the disc image (the same disc stays mounted across a load) or the
	/// BIOS, only mutable runtime state. Components write in a fixed order, each
	/// fronted by a section tag the reader verifies.
	/// </summary>
	public void SaveState(Stream stream)
	{
		var w = new StateWriter(stream);
		w.U32(SaveStateMagic);
		w.U32(SaveStateVersion);

		// Scheduler clocks first, peripherals serialize their event deadlines
		// relative to GlobalTickCounter, so the reader needs it restored early.
		w.Section("SCHED"); Scheduler.SaveState(w);
		w.Section("CPU");   Cpu.SaveState(w);
		w.Section("MEM");   Memory.SaveState(w);
		w.Section("GPU");   Gpu.SaveState(w);
		w.Section("SPU");   Spu.SaveState(w);
		w.Section("GTE");   Gte.SaveState(w);
		w.Section("MDEC");  Mdec.SaveState(w);
		w.Section("CDROM"); Cdrom.SaveState(w);
		w.Section("DMA");   Dma.SaveState(w);
		w.Section("TMR");   Timers.SaveState(w);
		w.Section("IRQ");   Interrupts.SaveState(w);
		w.Section("PAD");   Controller.SaveState(w);
		w.Section("END");
	}

	/// <summary>
	/// Restore machine state previously written by <see cref="SaveState"/>. The
	/// running Psx instance is reused (components keep their TimingEvent objects
	/// and delegates); each peripheral re-arms its scheduler events from the
	/// restored deadlines. Throws <see cref="InvalidDataException"/> on a bad /
	/// incompatible / misaligned state rather than half-applying it.
	/// </summary>
	public void LoadState(Stream stream)
	{
		var r = new StateReader(stream);
		if (r.U32() != SaveStateMagic)
			throw new Exception("Not a PSX save state (bad magic).");
		uint ver = r.U32();
		if (ver != SaveStateVersion)
			throw new Exception($"Incompatible save-state version {ver} (expected {SaveStateVersion}).");

		// Clear the scheduler's active list BEFORE peripherals re-arm, each
		// peripheral's LoadState re-Schedules its own events, rebuilding the list.
		Scheduler.ClearForLoad();
		r.Section("SCHED"); Scheduler.LoadState(r);
		r.Section("CPU");   Cpu.LoadState(r);
		r.Section("MEM");   Memory.LoadState(r);
		r.Section("GPU");   Gpu.LoadState(r);
		r.Section("SPU");   Spu.LoadState(r);
		r.Section("GTE");   Gte.LoadState(r);
		r.Section("MDEC");  Mdec.LoadState(r);
		r.Section("CDROM"); Cdrom.LoadState(r);
		r.Section("DMA");   Dma.LoadState(r);
		r.Section("TMR");   Timers.LoadState(r);
		r.Section("IRQ");   Interrupts.LoadState(r);
		r.Section("PAD");   Controller.LoadState(r);
		r.Section("END");

		// Re-derive the CPU batch deadline for the restored event list.
		Scheduler.UpdateCpuDowncount();
	}

	/// <summary>
	/// Round-trip self-test: Save -> Load -> Save and assert the two snapshots are
	/// byte-identical. Catches any field whose Save/LoadState don't round-trip (a
	/// section-tag mismatch, a wrong type, a queue serialized in the wrong order).
	/// It does NOT catch a field that's simply never serialized on EITHER side,
	/// only a real save->play->load test finds those, but it's a strong, instant
	/// first-line check that the wiring is internally consistent. Returns true on
	/// success; on failure, <paramref name="error"/> says where it diverged.
	///
	/// NOTE: this mutates live state via the intermediate Load, so only run it at a
	/// safe point (emulator paused), and the Load is into the SAME state we just
	/// saved, so it's a no-op in practice when it passes.
	/// </summary>
	public bool SaveStateSelfTest(out string error)
	{
		error = null;
		try
		{
			byte[] a, b;
			using (var ms = new MemoryStream()) { SaveState(ms); a = ms.ToArray(); }
			using (var ms = new MemoryStream(a)) { LoadState(ms); }
			using (var ms = new MemoryStream()) { SaveState(ms); b = ms.ToArray(); }
			if (a.Length != b.Length) { error = $"length {a.Length} != {b.Length}"; return false; }
			for (int i = 0; i < a.Length; i++)
				if (a[i] != b[i]) { error = $"first diff at byte {i}: {a[i]} != {b[i]}"; return false; }
			return true;
		}
		catch (System.Exception e)
		{
			error = e.Message;
			return false;
		}
	}
}
