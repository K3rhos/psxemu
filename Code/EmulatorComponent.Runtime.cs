using System.Threading;
using System.Threading.Tasks;

namespace PSXEmu;

public sealed partial class EmulatorComponent
{
	private readonly struct FramePacket;
	
	
	
	private async Task EmulationLoop()
	{
		var token = _cts.Token;

		try
		{
			while (!token.IsCancellationRequested)
			{
				await _frameSemaphore.WaitAsync(token);

				long emuFrameStart = PsxPerfMonitor.Stamp();

				var core = Core;

				if (core == null)
					break;

				Interlocked.Exchange(ref _workerInFrame, 1); // save/load handshake (see SaveStateToSlot)

				// Push current controller state
				core.Controller.ButtonMask = (ushort)Interlocked.CompareExchange(ref _buttonMask, 0, 0);

				core.RunFrame();

				// If auto trace is enabled, we stop here when we hit the configured frame cap.
				if (AutoTrace && core.Trace.Enabled && core.FrameCount >= AutoTraceFrames)
				{
					core.Trace.Disable();

					PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Info,
						$"[TRACE] Auto-trace stopped at frame {core.FrameCount} (cap = {AutoTraceFrames}).");
				}

				long snapshotStart = PsxPerfMonitor.Stamp();

				core.Gpu.SnapshotVram();

				core.Perf.AddTicks(PsxPerfSection.EmuSnapshotVram, PsxPerfMonitor.Stamp() - snapshotStart);

				if (token.IsCancellationRequested)
					break;

				int idx = _workerBufIdx;
				_workerBufIdx = (idx + 1) & 3;
				var aud = _audBufs[idx];

				int sampleCount = core.Spu.SamplesWritten;

				if (sampleCount > 0)
				{
					long audioCopyStart = PsxPerfMonitor.Stamp();

					Buffer.BlockCopy(core.Spu.OutputBuffer, 0, aud, 0,
						sampleCount * PsxConstants.SpuChannels * sizeof(short));

					EnqueueAudioSamples(aud.AsSpan(0, sampleCount * PsxConstants.SpuChannels));

					core.Perf.AddTicks(PsxPerfSection.EmuAudioCopy, PsxPerfMonitor.Stamp() - audioCopyStart);
				}

				Interlocked.Exchange(ref _workerInFrame, 0); // no Core access past this point until next frame

				long emuComputeEnd = PsxPerfMonitor.Stamp();

				core.Perf.AddTicks(PsxPerfSection.EmuFrameCompute, emuComputeEnd - emuFrameStart);

				await _frameChannel.Writer.WriteAsync(default, token);

				core.Perf.AddTicks(PsxPerfSection.EmuFrameQueueWait, PsxPerfMonitor.Stamp() - emuComputeEnd);

				Interlocked.Exchange(ref _lastWorkerFrameTick, PsxPerfMonitor.Stamp());

				core.Perf.AddTicks(PsxPerfSection.EmuFrame, PsxPerfMonitor.Stamp() - emuFrameStart);
			}
		}
		catch (OperationCanceledException)
		{
			
		}
		catch (Exception _Exception)
		{
			_workerFaultMessage = $"{_Exception.GetType().Name}: {_Exception.Message}";
			
			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Fatal, $"Emulation worker: {_Exception.Message}\n{_Exception.StackTrace}");
			
			_frameChannel.Writer.TryComplete(_Exception);
		}
	}
	
	
	
	private void WaitForWorkerIdle()
	{
		// Busy spin for a couple of ticks.
		for (int spins = 0; spins < 50_000_000 && Interlocked.CompareExchange(ref _workerInFrame, 0, 0) != 0; spins++) {}
	}
	
	
	
	public void ShutdownEmulator()
	{
		IsReady = false;
		
		_cts?.Cancel();
		_cts?.Dispose();
		_cts = null;

		if (_soundHandle.IsValid())
			_soundHandle.Volume = 0;

		_audioStream?.Dispose();
		_audioStream = null;

		_frameSemaphore?.Dispose();
		_frameSemaphore = null;
		_frameChannel = null;

		if (_camera.IsValid() && Core?.Gpu?.RenderCommandList != null)
			_camera.RemoveCommandList(Core.Gpu.RenderCommandList);

		Core?.Gpu?.DisposeGpu();
		Core?.Shutdown(); // flushes memory card to disk before discarding
		
		_camera = null;
		
		Core = null;
		ScreenTexture = null;
		PerfSummary = string.Empty;
		
		_workerFaultMessage = null;
	}
}
