namespace PSXEmu;

public enum PsxLogCategory
{
	PSX,
	CPU,
	GPU,
	SPU,
	CDROM,
	SBI,
	DMA,
	Timer,
	Memory,
	IO
}

[Flags]
public enum PsxLogLevel
{
	None = 0,
	Fatal = 1 << 0,
	Error = 1 << 1,
	Warn = 1 << 2,
	Info = 1 << 3,
	Debug = 1 << 4,
	GameError = 1 << 5
}

public static class PsxLog
{
	private static Action<PsxLogCategory, PsxLogLevel, string> m_Backend;

	public static void SetBackend(Action<PsxLogCategory, PsxLogLevel, string> _Backend) => m_Backend = _Backend;

	public static void Write(PsxLogCategory _Category, PsxLogLevel _Level, string _Message) => m_Backend?.Invoke(_Category, _Level, _Message);
}
