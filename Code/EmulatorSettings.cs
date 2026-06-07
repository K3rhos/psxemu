namespace PSXEmu;

public sealed class EmulatorSettings
{
	// Rendering
	public bool GPURasterizer { get; init; } = false;
	public int GpuRasterScale { get; init; } = 1;

	// Post Processing
	public PsxDisplayFilter DisplayFilter { get; init; } = PsxDisplayFilter.Bilinear;
	public float ScanlineStrength { get; init; } = 0.75f;
	public float ScanlineSharpness { get; init; } = 1.0f;
	public float ScanlineFrequency { get; init; } = 0.5f;
	public float PhosphorMaskStrength { get; init; } = 0.5f;
	public float CrtColorBoost { get; init; } = 1.1f;

	// Debug
	public bool ShowPerformanceOverlay { get; init; } = false;
	public EmulatorComponent.LogLevels LogLevel { get; init; } = EmulatorComponent.LogLevels.None;

	// Services
	public bool FetchGameCovers { get; init; } = true;
}
