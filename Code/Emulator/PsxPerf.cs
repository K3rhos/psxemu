using System.Diagnostics;
using System.Text;

namespace PSXEmu;

public enum PsxPerfSection
{
	EmuFrame,
	EmuFrameCompute,
	EmuRunFrame,
	EmuSnapshotVram,
	EmuAudioCopy,
	EmuFrameQueueWait,
	MainUpdate,
	MainDrainFrames,
	MainAudioWrite,
	MainGpuUpload,
	PsxFrameCpuRun,
	PsxFramePeripherals,
	PsxFrameVBlank,
	DmaGpu,
	DmaGpuRead,
	DmaSpu,
	DmaCdrom,
	DmaOtc,
	GpuUploadTotal,
	GpuUploadConvert,
	GpuUploadCpuToGpu,
	GpuUploadPreviewSync,
	GpuUploadRasterDispatch,
	GpuUploadDisplayDispatch,
}

public sealed class PsxPerfMonitor
{
	private sealed class Stat
	{
		public string Name;
		public double LastMs;
		public double AvgMs;
		public double MaxMs;
		public long Calls;
	}

	private readonly object _lock = new();
	private readonly Stat[] _stats;
	private readonly Stopwatch _reportStopwatch = Stopwatch.StartNew();
	private string _cachedSummary = string.Empty;
	private long _pacedFrameCount;
	private long _pacedFrameTicks;

	public PsxPerfMonitor()
	{
		var values = Enum.GetValues<PsxPerfSection>();
		_stats = new Stat[values.Length];
		for (int i = 0; i < values.Length; i++)
		{
			_stats[i] = new Stat { Name = values[i].ToString() };
		}
	}

	public static long Stamp() => Stopwatch.GetTimestamp();

	public void AddTicks(PsxPerfSection section, long elapsedTicks)
	{
		if (elapsedTicks <= 0)
			return;

		double ms = elapsedTicks * 1000.0 / Stopwatch.Frequency;
		var stat = _stats[(int)section];

		lock (_lock)
		{
			stat.LastMs = ms;
			stat.Calls++;
			stat.AvgMs = stat.Calls == 1 ? ms : (stat.AvgMs * 0.90) + (ms * 0.10);
			if (ms > stat.MaxMs)
				stat.MaxMs = ms;
		}
	}

	public void AddPacedFrameTicks(long elapsedTicks)
	{
		if (elapsedTicks <= 0)
			return;

		lock (_lock)
		{
			_pacedFrameCount++;
			_pacedFrameTicks += elapsedTicks;
		}
	}

	public string GetSummary()
	{
		lock (_lock)
		{
			if (_reportStopwatch.ElapsedMilliseconds < 250 && !string.IsNullOrEmpty(_cachedSummary))
				return _cachedSummary;

			var top = _stats
				.Where(s => s.Calls > 0)
				.OrderByDescending(s => s.AvgMs)
				.Take(10)
				.ToArray();

			var sb = new StringBuilder();
			sb.AppendLine("PSX Perf");

			var emuFrame = _stats[(int)PsxPerfSection.EmuFrame];
			if (emuFrame.Calls > 0)
			{
				double fps = emuFrame.AvgMs > 0.0001 ? 1000.0 / emuFrame.AvgMs : 0.0;
				sb.AppendLine($"Frame total      {emuFrame.AvgMs,6:0.00} ms  {fps,6:0.0} fps");
			}

			var emuCompute = _stats[(int)PsxPerfSection.EmuFrameCompute];
			if (emuCompute.Calls > 0)
			{
				double fps = emuCompute.AvgMs > 0.0001 ? 1000.0 / emuCompute.AvgMs : 0.0;
				sb.AppendLine($"Frame compute    {emuCompute.AvgMs,6:0.00} ms  {fps,6:0.0} fps");
			}

			if (_pacedFrameCount > 0 && _pacedFrameTicks > 0)
			{
				double pacedMs = (_pacedFrameTicks * 1000.0 / Stopwatch.Frequency) / _pacedFrameCount;
				double pacedFps = pacedMs > 0.0001 ? 1000.0 / pacedMs : 0.0;
				sb.AppendLine($"Frame paced      {pacedMs,6:0.00} ms  {pacedFps,6:0.0} fps");
			}

			foreach (var stat in top)
				sb.AppendLine($"{stat.Name,-24} {stat.AvgMs,6:0.00} ms  last {stat.LastMs,6:0.00}");

			_cachedSummary = sb.ToString();
			_reportStopwatch.Restart();
			return _cachedSummary;
		}
	}
}
