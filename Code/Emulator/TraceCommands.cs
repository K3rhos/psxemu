namespace PSXEmu;

using System.Linq;
using Sandbox;

/// <summary>
/// Console commands for driving <see cref="EmulatorTrace"/> from the s&box
/// console. All commands operate on the FIRST <see cref="EmulatorComponent"/>
/// found in the active scene.
///
/// Usage from the s&box console:
/// <code>
///   trace_start traces/re2-baseline.txt               # start, default 60-frame dump interval
///   trace_start traces/re2-baseline.txt 1             # dump every frame (cycle-accurate diff)
///   trace_checkpoint after-disc-load                  # custom marker
///   trace_flush                                        # force write to disk now
///   trace_stop                                         # stop + final flush
///   trace_status                                       # print current state
/// </code>
///
/// Files end up under the s&box project's <c>data/</c> folder. See
/// <see cref="EmulatorTrace"/> for the output format and intended workflow.
/// </summary>
public static class TraceCommands
{
	/// <summary>Locate the live emulator instance in the active scene, or null if none.</summary>
	private static Psx FindPsx()
	{
		var scene = Game.ActiveScene;
		if (scene == null) return null;
		var comp = scene.GetAllComponents<EmulatorComponent>().FirstOrDefault();
		return comp?.Core;
	}

	[ConCmd("trace_start")]
	public static void Start(string path, int dumpEveryFrames = 60)
	{
		var psx = FindPsx();
		if (psx == null)
		{
			Log.Warning("[PSX/TRACE] No active EmulatorComponent found.");
			return;
		}
		if (string.IsNullOrWhiteSpace(path))
		{
			Log.Warning("[PSX/TRACE] Usage: trace_start <path> [dumpEveryFrames]");
			return;
		}
		psx.Trace.Enable(path, dumpEveryFrames);
		Log.Info($"[PSX/TRACE] Started -> data/{path} (dump every {dumpEveryFrames} frames)");
	}

	[ConCmd("trace_stop")]
	public static void Stop()
	{
		var psx = FindPsx();
		if (psx?.Trace == null)
		{
			Log.Warning("[PSX/TRACE] No active EmulatorComponent.");
			return;
		}
		if (!psx.Trace.Enabled)
		{
			Log.Info("[PSX/TRACE] Not running.");
			return;
		}
		psx.Trace.Disable();
		Log.Info("[PSX/TRACE] Stopped (final flush done).");
	}

	[ConCmd("trace_flush")]
	public static void Flush()
	{
		var psx = FindPsx();
		if (psx?.Trace == null || !psx.Trace.Enabled)
		{
			Log.Warning("[PSX/TRACE] Not running.");
			return;
		}
		psx.Trace.Flush();
		Log.Info("[PSX/TRACE] Flushed to disk.");
	}

	[ConCmd("trace_checkpoint")]
	public static void Checkpoint(string label)
	{
		var psx = FindPsx();
		if (psx?.Trace == null || !psx.Trace.Enabled)
		{
			Log.Warning("[PSX/TRACE] Not running, start a trace first with `trace_start`.");
			return;
		}
		if (string.IsNullOrWhiteSpace(label))
		{
			Log.Warning("[PSX/TRACE] Usage: trace_checkpoint <label>");
			return;
		}
		psx.Trace.Checkpoint(label);
		Log.Info($"[PSX/TRACE] Checkpoint: {label}");
	}

	[ConCmd("trace_status")]
	public static void Status()
	{
		var psx = FindPsx();
		if (psx == null)
		{
			Log.Info("[PSX/TRACE] No active EmulatorComponent.");
			return;
		}
		Log.Info($"[PSX/TRACE] Enabled={psx.Trace.Enabled}, frame={psx.FrameCount}, cycles={psx.Cpu.Cycles}");
	}
}
