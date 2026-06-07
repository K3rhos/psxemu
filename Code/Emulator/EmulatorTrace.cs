namespace PSXEmu;

using System;
using System.Text;
using Sandbox;

/// <summary>
/// Deterministic emulator state-dump facility for cross-run / cross-emulator
/// comparison. CPU/DMA timing accuracy work, without this, every change
/// requires manual testing of each game in the corpus. With it, two runs of the
/// same game can be diffed line-by-line to spot timing divergence.
///
/// Usage:
/// <code>
///   var trace = psx.Trace;
///   trace.Enable("traces/baseline-re2.txt", dumpEveryFrames: 60);
///   // ... emulator runs ...
///   trace.Disable();  // flushes to disk
/// </code>
///
/// Output goes to <c>FileSystem.Data/&lt;path&gt;</c>. StreamWriter / System.IO
/// is blacklisted under s&box's sandbox, so we buffer the trace in a
/// StringBuilder and flush via FileSystem.Data.WriteAllBytes (UTF-8).
/// Disable() and the periodic auto-flush keep the file on disk current;
/// an abrupt shutdown (editor crash) loses up to <see cref="AutoFlushFrames"/>
/// frames of pending trace.
///
/// Per-checkpoint output is a sequence of <c>key=value</c> lines so two
/// runs can be diffed directly (line-order is stable within a checkpoint).
/// Each checkpoint header carries the emulated frame number, so divergence
/// can be pinpointed in time.
///
/// Zero runtime overhead when <see cref="Enabled"/> is false, the <see cref="OnFrameEnd"/>
/// hook returns immediately on the null-buffer check. Safe to leave
/// the call site in Psx.RunFrame permanently.
///
/// </summary>
public sealed class EmulatorTrace
{
	// Number of frames between automatic flushes to disk. Tunes the
	// loss-on-crash window vs the FileSystem write overhead.
	// 600 frames @ 60fps = ~10 emulated seconds, short enough that a
	// crash loses at most a small slice of the trace.
	private const int AutoFlushFrames = 600;

	private readonly Psx _psx;
	private StringBuilder _buffer;
	private string _path;
	private int _dumpEveryNFrames;
	private int _frameCounter;
	private int _framesSinceFlush;

	/// <summary>True if a trace is currently being captured.</summary>
	public bool Enabled => _buffer != null;

	public EmulatorTrace(Psx psx)
	{
		_psx = psx;
	}

	/// <summary>
	/// Start a trace, dumping a checkpoint every <paramref name="dumpEveryFrames"/>
	/// emulated frames. Path is relative to FileSystem.Data. Overwrites
	/// any existing file at the path. Re-Enable replaces the previous trace.
	/// </summary>
	public void Enable(string path, int dumpEveryFrames = 60)
	{
		Disable();
		_buffer = new StringBuilder(64 * 1024);
		_path = path;
		_dumpEveryNFrames = System.Math.Max(1, dumpEveryFrames);
		_frameCounter = 0;
		_framesSinceFlush = 0;
		WriteHeader();
		FlushToDisk();
	}

	/// <summary>Stop the trace and flush buffered output to disk.</summary>
	public void Disable()
	{
		if (_buffer == null) return;
		FlushToDisk();
		_buffer = null;
		_path = null;
	}

	/// <summary>
	/// Force an immediate flush to disk without disabling the trace.
	/// Auto-called every <see cref="AutoFlushFrames"/> frames; call manually
	/// before a known-risky test to keep the trace current.
	/// </summary>
	public void Flush()
	{
		if (_buffer == null) return;
		FlushToDisk();
	}

	/// <summary>
	/// Hook called from <c>Psx.RunFrame</c> at the end of each emulated frame.
	/// No-op when disabled. Writes a checkpoint every Nth frame per the
	/// <c>Enable</c> setting, and flushes to disk every <see cref="AutoFlushFrames"/>
	/// frames.
	/// </summary>
	internal void OnFrameEnd()
	{
		if (_buffer == null) return;
		_frameCounter++;
		_framesSinceFlush++;
		if (_frameCounter >= _dumpEveryNFrames)
		{
			_frameCounter = 0;
			DumpCheckpoint($"frame={_psx.FrameCount}");
		}
		if (_framesSinceFlush >= AutoFlushFrames)
		{
			_framesSinceFlush = 0;
			FlushToDisk();
		}
	}

	/// <summary>
	/// Force a checkpoint dump with a custom label. Useful for marking
	/// game-specific events ("after-cdrom-init", "fmv-cmd1-first", etc.)
	/// in the trace.
	/// </summary>
	public void Checkpoint(string label)
	{
		if (_buffer == null) return;
		DumpCheckpoint(label);
	}

	private void WriteHeader()
	{
		_buffer.AppendLine($"# PSXEmu trace start = {DateTime.UtcNow:o}");
		_buffer.AppendLine($"# dump_every_n_frames={_dumpEveryNFrames}");
		_buffer.AppendLine($"# Format: key=value, one per line, blank line between checkpoints.");
		_buffer.AppendLine($"# Lines are sorted within each checkpoint for stable diffing.");
		_buffer.AppendLine();
	}

	private void FlushToDisk()
	{
		try
		{
			// Ensure parent directory exists (FileSystem.Data is rooted under
			// the project's data folder; subdirectories don't auto-create).
			string dir = System.IO.Path.GetDirectoryName(_path);
			if (!string.IsNullOrEmpty(dir) && !FileSystem.Data.DirectoryExists(dir))
				FileSystem.Data.CreateDirectory(dir);
			// Write via WriteAllBytes(UTF-8), confirmed pattern from
			// PsxMemoryCard.Flush; WriteAllText may not be in s&box's
			// FileSystem API surface.
			byte[] bytes = Encoding.UTF8.GetBytes(_buffer.ToString());
			FileSystem.Data.WriteAllBytes(_path, bytes);
		}
		catch (Exception ex)
		{
			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Warn,
				$"[TRACE] Failed to flush {_path}: {ex.Message}");
		}
	}

	private void DumpCheckpoint(string label)
	{
		var b = _buffer;
		b.AppendLine($"[CHECKPOINT {label}]");

		// CPU: the fundamental cycle counter. GlobalTickCounter is what
		// the scheduler operates in; PendingTicks is the not-yet-committed
		// delta. Their SUM is the "real now" from the CPU's perspective.
		b.AppendLine($"cpu.cycles={_psx.Cpu.Cycles}");
		b.AppendLine($"cpu.pending_ticks={_psx.Cpu.PendingTicks}");
		b.AppendLine($"cpu.downcount={_psx.Cpu.Downcount}");
		b.AppendLine($"cpu.pc=0x{_psx.Cpu.Pc:X8}");
		b.AppendLine($"cpu.sr=0x{_psx.Cpu.Sr:X8}");
		b.AppendLine($"cpu.cause=0x{_psx.Cpu.Cause:X8}");
		b.AppendLine($"cpu.epc=0x{_psx.Cpu.Epc:X8}");

		// Scheduler: fundamental for verifying scheduler-driven changes.
		b.AppendLine($"sched.global_tick={_psx.Scheduler.GlobalTickCounter}");
		b.AppendLine($"sched.event_run_tick={_psx.Scheduler.EventRunTickCounter}");

		// GPU: DrawCmdCount resets each frame so it's "per-last-frame" not cumulative.
		b.AppendLine($"gpu.draw_cmd_count={_psx.Gpu.DrawCmdCount}");
		b.AppendLine($"gpu.display_24bit={(_psx.Gpu.IsDisplay24Bit ? 1 : 0)}");

		// SPU: running totals + current state.
		b.AppendLine($"spu.samples_written={_psx.Spu.SamplesWritten}");
		b.AppendLine($"spu.xa_queue_length={_psx.Spu.XaQueueLength}");

		// MDEC: cumulative command counts and macroblock count.
		b.AppendLine($"mdec.cmd1_count={_psx.Mdec.DiagCmd1Count}");

		// CDROM: current state + last LBA delivered.
		b.AppendLine($"cdrom.iflags=0x{_psx.Cdrom.DiagIFlags:X2}");
		b.AppendLine($"cdrom.ienable=0x{_psx.Cdrom.DiagIEnable:X2}");
		b.AppendLine($"cdrom.reading={(_psx.Cdrom.DiagReading ? 1 : 0)}");
		b.AppendLine($"cdrom.sector_pending={(_psx.Cdrom.DiagSectorPending ? 1 : 0)}");
		b.AppendLine($"cdrom.cmd_pending={(_psx.Cdrom.DiagCmdPending ? 1 : 0)}");
		b.AppendLine($"cdrom.last_cmd=0x{_psx.Cdrom.DiagLastCmd:X2}");
		b.AppendLine($"cdrom.last_lba={_psx.Cdrom.DiagLastLba}");

		// DMA: DICR (interrupt control / flags) + per-channel pending blocks.
		b.AppendLine($"dma.dicr=0x{_psx.Dma.DiagDicr:X8}");
		for (int i = 0; i < 7; i++)
			b.AppendLine($"dma.ch{i}.pending_blocks={_psx.Dma.Channels[i].PendingBlocks}");

		// Interrupt controller: IStat/IMask + per-IRQ-source raise/ack
		// counts (huge tell for "IRQ is being raised but not acked" bugs).
		b.AppendLine($"int.istat=0x{_psx.Interrupts.IStat:X3}");
		b.AppendLine($"int.imask=0x{_psx.Interrupts.IMask:X3}");
		for (int i = 0; i < 11; i++)
		{
			b.AppendLine($"int.bit{i}.raise={_psx.Interrupts.DiagRaiseCount[i]}");
			b.AppendLine($"int.bit{i}.ack={_psx.Interrupts.DiagAckCount[i]}");
		}

		// Frame counter for easy correlation with logs.
		b.AppendLine($"frame={_psx.FrameCount}");
		b.AppendLine();
	}
}
