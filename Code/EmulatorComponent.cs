using System.Threading;
using System.Threading.Channels;
using System.Text;

namespace PSXEmu;

public enum PsxDisplayFilter
{
	Nearest = 0,
	Bilinear = 1
}

public sealed partial class EmulatorComponent : Component, IHotloadManaged
{
	private string _biosPath = "bios/scph1001.bin";
	private string _discPath;

	// Hot-reload relaunch bookkeeping (see the IHotloadManaged methods below).
	private bool _hotloadRelaunchPending;
	private string _hotloadRelaunchBios;
	private string _hotloadRelaunchDisc;

	private SoundStream _audioStream;
	private SoundHandle _soundHandle;
	private CameraComponent _camera;

	private CancellationTokenSource _cts;
	private Channel<FramePacket> _frameChannel;
	private SemaphoreSlim _frameSemaphore;

	// Atomic input state: button mask (active-low)
	private int _buttonMask = unchecked((int)0xFFFF);

	// Audio double-buffering
	private short[][] _audBufs;
	private int _workerBufIdx;
	private short[] _audioRingBuffer;
	private short[] _audioDrainBuffer;
	private readonly object _audioRingLock = new();
	private int _audioRingReadPos;
	private int _audioRingWritePos;
	private int _audioRingCount;

	private double _frameDebt;
	private bool _paused;
	// Set (1) by the emulation worker while it's inside its per-frame Core block;
	// the save/load path waits for this to clear (with _paused=true) before it
	// touches Core from the UI thread. Accessed via Interlocked for cross-thread
	// visibility, `volatile` is NOT s&box-whitelisted. See SaveStateToSlot.
	private int _workerInFrame;
	private int _inputCooldown;
	private long _lastWorkerFrameTick;
	private string _workerFaultMessage;
	private long _lastPresentedFrameTick;
	private readonly List<LaunchEntry> _availableBios = [];
	private readonly List<LaunchEntry> _availableGames = [];
	private WebSocketClient _webSocketClient;

	private const int AudioTargetQueuedSamples = 12000;
	private const int AudioRingSeconds = 2;
	private const int AudioPrefillFrames = 6;
	private const int PsxBiosSize = 512 * 1024; // 512 KB
	private const int MaxCoverBytes = 1024 * 1024; // 1 MB

	public static EmulatorComponent Current { get; private set; }
	public Psx Core { get; private set; }
	public Texture ScreenTexture { get; private set; }
	public bool IsReady { get; private set; }
	public bool IsLaunching { get; private set; }
	public bool IsRefreshing { get; private set; }
	public string ErrorMessage { get; private set; }
	public string PerfSummary { get; private set; }
	public string SelectedBiosPath { get; private set; }
	public string SelectedDiscPath { get; private set; }
	public bool IsRefreshingCovers { get; private set; }
	public IReadOnlyList<LaunchEntry> AvailableBios => _availableBios;
	public IReadOnlyList<LaunchEntry> AvailableGames => _availableGames;

	protected override void OnStart()
	{
		Current = this;

		// Make sure bios and roms directory exists by default
		FileSystem.Data.CreateDirectory("bios");
		FileSystem.Data.CreateDirectory("roms");
		
		// Restore persisted user settings before anything reads them.
		LoadSettings();

		PsxLog.SetBackend(LogBackend);
		_webSocketClient = CreateCoverClient();

		RefreshLaunchLibrary();
	}

	protected override void OnDisabled()
	{
		_webSocketClient?.Dispose();
		_webSocketClient = null;

		if (ReferenceEquals(Current, this))
			Current = null;

		base.OnDisabled();
	}

	// --- Hot-reload handling (IHotloadManaged) ---
	//
	// s&box recompiles and recreates this component on every code edit, but the
	// EmulationLoop runs on a detached GameTask thread that captured the OLD
	// instance and its OLD-code Core, so it keeps executing pre-edit code until a
	// full editor restart. We cancel that loop when the old instance is torn down
	// and relaunch a fresh one (new code + reloaded program) on the new instance.

	void IHotloadManaged.Destroyed(Dictionary<string, object> state)
	{
		// Remember what to relaunch if emulation was running, then stop the old
		// detached loop so it can't keep running stale code.
		if (_cts != null && Core != null)
		{
			state["emu_bios"] = _biosPath;
			state["emu_disc"] = _discPath;
		}
		_cts?.Cancel();
	}

	void IHotloadManaged.Created(IReadOnlyDictionary<string, object> state)
	{
		// New instance on the recompiled code. If emulation was running, flag a
		// fresh relaunch (performed on the main thread in OnUpdate, where launching
		// is valid) so it rebuilds Core + loop on the new code and reloads the EXE.
		if (state.GetValueOrDefault("emu_bios") is string bios)
		{
			_hotloadRelaunchBios = bios;
			_hotloadRelaunchDisc = state.GetValueOrDefault("emu_disc") as string;
			_hotloadRelaunchPending = true;
		}
	}

	protected override void OnUpdate()
	{
		try
		{
			if (_hotloadRelaunchPending)
			{
				_hotloadRelaunchPending = false;
				PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Info,
					"Hot-reload detected: relaunching emulation on the recompiled code.");
				LaunchSelection(_hotloadRelaunchBios, _hotloadRelaunchDisc);
				return;
			}
			OnUpdateInner();
		}
		catch (Exception ex)
		{
			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Error, $"OnUpdate exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
		}
	}

	private void OnUpdateInner()
	{
		if (!IsReady || Core == null) return;
		long updateStart = PsxPerfMonitor.Stamp();

		PollInput();
		SyncGpuDisplaySettings();

		// Re-init audio stream if it dies
		if (_audioStream != null && !_soundHandle.IsValid)
		{
			try { InitAudioStream(); }
			catch { _audioStream = null; }
		}

		// Accumulate frame debt and release semaphores.
		//
		// Burst-recovery rules:
		//   * `maxDebt = frameTime * 2`, cap at 2 frames of accumulated debt,
		//     down from 3. The PsxConstants frame-rate fix removed the
		//     steady-state drift component, so debt now only accumulates from
		//     real host hitches. 2 frames is enough headroom to absorb a
		//     33 ms hitch without losing the catch-up entirely.
		//   * Audio-aware throttle inside the loop, if the ring buffer is
		//     already past 50 % full, stop releasing more frames and drop
		//     the rest of the debt. Otherwise the worker would produce 2-3
		//     frames worth of audio (1,500-2,200 stereo samples) in rapid
		//     succession before the host audio mixer drains, eventually
		//     overflowing the ring and forcing `EnqueueAudioSamples` to
		//     discard old samples, audible as micro-clicks during long
		//     FMVs. Letting emulated time slide briefly behind wall-clock
		//     is far preferable to glitching the audio output.
		if (!_paused)
		{
			_frameDebt += RealTime.Delta;
			double frameTime = Core.FrameTime;
			double maxDebt = frameTime * 2;
			if (_frameDebt > maxDebt) _frameDebt = maxDebt;

			int ringCapacity = _audioRingBuffer?.Length ?? 0;
			int ringHalfFull = ringCapacity > 0 ? ringCapacity / 2 : int.MaxValue;

			while (_frameDebt >= frameTime)
			{
				// Dirty read on `_audioRingCount` is fine here, the value is
				// only a throttling hint, and it's an atomic int load on the
				// platforms we target; the lock-protected accessor would just
				// add contention with the worker's writes.
				if (_audioRingCount > ringHalfFull)
				{
					// Audio ring filling faster than the host can drain.
					// Drop the remaining debt so we don't queue more frames.
					_frameDebt = 0;
					break;
				}

				_frameDebt -= frameTime;

				if (_frameSemaphore.CurrentCount < 4)
					_frameSemaphore.Release();
			}
		}

		// Drain all pending frames from the emulation thread
		bool hasFrame = false;
		long drainStart = PsxPerfMonitor.Stamp();

		while (_frameChannel != null && _frameChannel.Reader.TryRead(out var frame))
		{
			hasFrame = true;
		}
		Core.Perf.AddTicks(PsxPerfSection.MainDrainFrames, PsxPerfMonitor.Stamp() - drainStart);
		PumpAudioStream();

		if (hasFrame)
		{
			long now = PsxPerfMonitor.Stamp();
			long lastPresented = Interlocked.Exchange(ref _lastPresentedFrameTick, now);
			if (lastPresented != 0)
				Core.Perf.AddPacedFrameTicks(now - lastPresented);
			
			long uploadStart = PsxPerfMonitor.Stamp();
			Core.Gpu?.UploadAndBuildCommandList();
			Core.Perf.AddTicks(PsxPerfSection.MainGpuUpload, PsxPerfMonitor.Stamp() - uploadStart);
		}
		else
			Core.Gpu?.RenderCommandList?.Reset();

		PerfSummary = ShowPerformanceOverlay ? BuildPerfSummary() : string.Empty;
		Core.Perf.AddTicks(PsxPerfSection.MainUpdate, PsxPerfMonitor.Stamp() - updateStart);
	}

	private void SyncGpuDisplaySettings()
	{
		var gpu = Core?.Gpu;
		if (gpu == null || !gpu.GpuReady)
			return;

		gpu.ApplyDisplaySettings(
			DisplayFilter,
			ScanlineStrength,
			ScanlineSharpness,
			ScanlineFrequency,
			PhosphorMaskStrength,
			CrtColorBoost);
	}

	private string BuildPerfSummary()
	{
		var sb = new StringBuilder();
		long lastWorkerTick = Interlocked.Read(ref _lastWorkerFrameTick);

		if (!string.IsNullOrEmpty(_workerFaultMessage))
		{
			sb.AppendLine($"Worker fault: {_workerFaultMessage}");
		}
		else if (lastWorkerTick == 0)
		{
			sb.AppendLine("Worker: no completed frame yet");
		}
		else
		{
			double msSinceWorkerFrame = (PsxPerfMonitor.Stamp() - lastWorkerTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			if (msSinceWorkerFrame > 1000.0)
				sb.AppendLine($"Worker: stalled ({msSinceWorkerFrame:0} ms since last frame)");
			else
				sb.AppendLine($"Worker: running ({msSinceWorkerFrame:0} ms since last frame)");
		}

		if (_audioStream != null)
			sb.AppendLine($"Audio queued: {_audioStream.QueuedSampleCount}");
		sb.AppendLine($"Audio ring: {GetAudioRingCount()}");
		sb.AppendLine($"SPU samples: {Core.Spu.LastFrameSamplesWritten}");
		sb.AppendLine($"SPU voices: {Core.Spu.LastFrameActiveVoices}  on:{Core.Spu.LastFrameKeyOns} off:{Core.Spu.LastFrameKeyOffs}");
		sb.AppendLine($"SPU peak: {Core.Spu.LastFramePeakAbs}  clipped: {Core.Spu.LastFrameClippedSamples}");
		sb.AppendLine($"Video: {Core.Gpu.VideoStandard}  target={Core.TargetFps:0.00}fps  actual={Core.EmulatedFps:0.00}fps");

		sb.Append(Core.Perf.GetSummary());
		return sb.ToString();
	}

	public void SetPaused(bool paused)
	{
		_paused = paused;
		if (paused)
		{
			_frameDebt = 0;
			if (_soundHandle.IsValid()) _soundHandle.Volume = 0;
		}
		else
		{
			_inputCooldown = 2;
			if (_soundHandle.IsValid()) _soundHandle.Volume = 1.0f;
		}
	}

	public void ResetEmulator() => Core?.Reset();

	protected override void OnDestroy()
	{
		ShutdownEmulator();
	}

	private void LogBackend(PsxLogCategory cat, PsxLogLevel level, string msg)
	{
		if (LogLevel == LogLevels.None) return;
		if (LogLevel == LogLevels.TTYOnly && !msg.Contains("[TTY]")) return;

		string text = $"[{cat}] {msg}";
		if ((level & (PsxLogLevel.Fatal | PsxLogLevel.Error)) != 0)
			Log.Error(text);
		else if ((level & (PsxLogLevel.Warn | PsxLogLevel.GameError)) != 0)
			Log.Warning(text);
		else
			Log.Info(text);
	}

}
