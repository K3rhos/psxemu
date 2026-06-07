namespace PSXEmu;

public sealed partial class EmulatorComponent
{
	public enum LogLevels
	{
		None,
		TTYOnly,
		All
	}

	private const string SettingsFile = "settings.json";
	
	[Property, Title("Performance Overlay"), Group("Debug")]
	public bool ShowPerformanceOverlay { get; set; } = false;

	[Property, Group("Debug")]
	public LogLevels LogLevel { get; set; } = LogLevels.None;

	// When true, it starts recording a game trace that will
	// give us important data to try and fix issues on a specific game.
	// Output path is "traces/auto/{game-serial}.txt"
	[Property, Title("Auto-Trace On Boot"), Group("Debug")]
	public bool AutoTrace { get; set; } = false;

	// Frames interval where we want to record a trace block.
	[Property, Title("Auto-Trace Frame Interval"), Range(1, 600), Group("Debug")]
	public int AutoTraceFrameInterval { get; set; } = 60;

	// Stop auto-trace after this many emulated frames.
	// 18000 with AutoTraceFrameInterval at 60 => 5 minutes
	[Property, Title("Auto-Trace Max Frames"), Group("Debug")]
	public int AutoTraceFrames { get; set; } = 18000;

	// When enabled, polygons are submitted as GPU vertex batches
	// and rasterized in psx_raster.shader instead of the CPU software rasterizer.
	// Required for internal-resolution upscaling.
	[Property, Title("GPU Rasterizer"), Group("Rendering")]
	public bool GPURasterizer { get; set; } = false;

	// Internal render-resolution multiplier for the GPU rasterizer.
	// 1x = native 240p, 3x = 720p, 5x = 1080p, 6x = 1440p, 9x = 2160p...
	// CPU rasterizer ignores this, it always runs at native resolution.
	[Property, Title("GPU Rasterizer Scale"), Range(1, 9), Group("Rendering")]
	public int GpuRasterScale { get; set; } = 1;

	[Property, Title("Display Filter"), Group("Post Processing")]
	public PsxDisplayFilter DisplayFilter { get; set; } = PsxDisplayFilter.Bilinear;

	[Property, Title("Scanline Strength"), Range(0f, 1f), Group("Post Processing")]
	public float ScanlineStrength { get; set; } = 0.75f;

	[Property, Title("Scanline Sharpness"), Range(0.5f, 8f), Group("Post Processing")]
	public float ScanlineSharpness { get; set; } = 1.0f;

	[Property, Title("Scanline Frequency"), Range(0f, 8f), Group("Post Processing")]
	public float ScanlineFrequency { get; set; } = 0.5f;

	[Property, Title("Phosphor Mask Strength"), Range(0f, 1f), Group("Post Processing")]
	public float PhosphorMaskStrength { get; set; } = 0.5f;

	[Property, Title("CRT Color Boost"), Range(1f, 2f), Group("Post Processing")]
	public float CrtColorBoost { get; set; } = 1.1f;

	[Property, Title("Fetch Game Covers"), Group("Covers")]
	public bool FetchGameCovers { get; set; } = true;
	
	[Property, Title("Scan For PSX-EXE"), Group("Games")]
	public bool ScanForPSXEXE { get; set; } = false;

	[Property, Title("Covers WebSocket URI"), Group("Covers")]
	public string CoversWebSocketUri { get; set; } = "ws://localhost:8080/";

	[Property, Title("Use Localhost Covers In Editor"), Group("Covers")]
	public bool UseLocalhostCoversInEditor { get; set; } = true;
	
	
	
	public void SaveSettings()
	{
		try
		{
			FileSystem.Data.WriteJson(SettingsFile, new EmulatorSettings
			{
				GPURasterizer = GPURasterizer,
				GpuRasterScale = GpuRasterScale,
				DisplayFilter = DisplayFilter,
				ScanlineStrength = ScanlineStrength,
				ScanlineSharpness = ScanlineSharpness,
				ScanlineFrequency = ScanlineFrequency,
				PhosphorMaskStrength = PhosphorMaskStrength,
				CrtColorBoost = CrtColorBoost,
				ShowPerformanceOverlay = ShowPerformanceOverlay,
				LogLevel = LogLevel,
				FetchGameCovers = FetchGameCovers,
			});
		}
		catch (Exception _Exception)
		{
			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Warn, $"Settings save failed: {_Exception.Message}");
		}
	}
	
	
	
	private void LoadSettings()
	{
		try
		{
			if (!FileSystem.Data.FileExists(SettingsFile))
				return;
			
			var s = FileSystem.Data.ReadJson<EmulatorSettings>(SettingsFile);
			
			if (s == null)
				return;

			GPURasterizer          = s.GPURasterizer;
			GpuRasterScale         = Math.Clamp(s.GpuRasterScale, 1, 16);
			DisplayFilter          = s.DisplayFilter;
			ScanlineStrength       = Math.Clamp(s.ScanlineStrength, 0f, 1f);
			ScanlineSharpness      = Math.Clamp(s.ScanlineSharpness, 0.5f, 8f);
			ScanlineFrequency      = Math.Clamp(s.ScanlineFrequency, 0f, 8f);
			PhosphorMaskStrength   = Math.Clamp(s.PhosphorMaskStrength, 0f, 1f);
			CrtColorBoost          = Math.Clamp(s.CrtColorBoost, 1f, 2f);
			ShowPerformanceOverlay = s.ShowPerformanceOverlay;
			LogLevel               = s.LogLevel;
			FetchGameCovers        = s.FetchGameCovers;
		}
		catch (Exception _Exception)
		{
			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Warn, $"Settings load failed: {_Exception.Message}");
		}
	}
}
