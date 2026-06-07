namespace PSXEmu;

public class PsxCdrom
{
	// =====================================================================
	// Constants
	// =====================================================================
	public const int RawSectorSize = 2352;
	public const int DataSectorSize = 2048;

	private const int SectorSyncSize = 12;
	private const int Mode1HeaderSize = 4;
	private const int Mode2HeaderSize = 12;       // 4 header + 4 subhdr + 4 subhdr dup
	private const int RawSectorOutputSize = RawSectorSize - SectorSyncSize; // 2340
	private const int SubqSectorSkew = 2;
	private const int XaSamplesPerSector4Bit = 4032;
	private const int ParamFifoSize = 16;
	private const int ResponseFifoSize = 16;
	private const int NumSectorBuffers = 8;
	private const int SingleSpeedSectorsPerSecond = 75;
	private const int DoubleSpeedSectorsPerSecond = 150;
	private const int MinimumInterruptDelay = 1000;
	private const int InterruptDeliveryCycles = 500;
	private const int MissedInt1DelayCycles = 5000;
	private const int InitTicks = 4_000_000;
	private const int IdReadTicks = 33_868;
	private const int MotorOnResponseTicks = 400_000;
	private const int MinSeekTicks = 30_000;
	private const int CddaReportStartDelay = 60;
	private const byte InterruptRegisterMask = 0x1F;

	// Interrupt reason codes
	private const byte IntDataReady = 1;
	private const byte IntComplete = 2;
	private const byte IntAck = 3;
	private const byte IntDataEnd = 4;
	private const byte IntError = 5;

	// Status bits (secondary status returned via GetStat)
	private const byte StatError = 1 << 0;
	private const byte StatMotorOn = 1 << 1;
	private const byte StatSeekError = 1 << 2;
	private const byte StatIdError = 1 << 3;
	private const byte StatShellOpen = 1 << 4;
	private const byte StatReading = 1 << 5;
	private const byte StatSeeking = 1 << 6;
	private const byte StatPlayingCdda = 1 << 7;

	private const byte ErrorReasonInvalidArgument = 0x10;
	private const byte ErrorReasonIncorrectNumberOfParameters = 0x20;
	private const byte ErrorReasonInvalidCommand = 0x40;
	private const byte ErrorReasonNotReady = 0x80;

	private enum DriveState : byte
	{
		Idle,
		ShellOpening,
		SeekingPhysical,
		SeekingLogical,
		Reading,
		Playing,
		ChangingSession,
		SpinningUp,
		SeekingImplicit,
		ChangingSpeedOrTOCRead,
	}

	/// <summary>One entry in the disc's Table of Contents (from the .cue file or defaulted).</summary>
	public readonly struct DiscTrack(byte number, bool isAudio, uint startLba, uint physicalStartSector)
	{
		public readonly byte Number = number;
		public readonly bool IsAudio = isAudio;
		public readonly uint StartLba = startLba;
		public readonly uint PhysicalStartSector = physicalStartSector;

		public DiscTrack(byte number, bool isAudio, uint startLba) : this(number, isAudio, startLba, startLba)
		{
		}
	}

	private sealed class SectorBuffer
	{
		public readonly byte[] Data = new byte[RawSectorOutputSize];
		public int Position;
		public int Size;

		public void SaveState(StateWriter w) { w.Bytes(Data); w.S32(Position); w.S32(Size); }
		public void LoadState(StateReader r) { r.Bytes(Data); Position = r.S32(); Size = r.S32(); }
	}

	/// <summary>Subchannel-Q used by GetlocP and SBI replacement path. BCD bytes.</summary>
	private struct SubChannelQ
	{
		public byte TrackNumberBcd;
		public byte IndexNumberBcd;
		public byte RelativeMinuteBcd;
		public byte RelativeSecondBcd;
		public byte RelativeFrameBcd;
		public byte AbsoluteMinuteBcd;
		public byte AbsoluteSecondBcd;
		public byte AbsoluteFrameBcd;
		public byte ControlBits;
		public bool IsCrcValid;

		public bool IsData => (ControlBits & 0x40) != 0;
	}

	/// <summary>Sector header (mm/ss/ff/mode) at offset SECTOR_SYNC_SIZE in a raw sector.</summary>
	private struct SectorHeader
	{
		public byte Minute;
		public byte Second;
		public byte Frame;
		public byte SectorMode;
	}

	/// <summary>Subheader from a Mode-2 sector at offset SYNC_SIZE+SECTOR_HEADER_SIZE.</summary>
	private struct XaSubHeader
	{
		public byte FileNumber;
		public byte ChannelNumber;
		public byte SubmodeBits;
		public byte CodinginfoBits;
		
		public bool SubmodeAudio => (SubmodeBits & 0x04) != 0;
		public bool SubmodeRealtime => (SubmodeBits & 0x40) != 0;
		public bool SubmodeEof => (SubmodeBits & 0x80) != 0;

		public bool CodingStereo => (CodinginfoBits & 0x01) != 0;
		public bool CodingHalfSampleRate => (CodinginfoBits & 0x04) != 0;
		public bool Coding8BitAdpcm => (CodinginfoBits & 0x10) != 0;
	}

	/// <summary>Minimal queue. Backed by a fixed array; pushes wrap when removed.</summary>
	private sealed class CdromFifo
	{
		private readonly byte[] _data;
		private int _head;
		private int _tail;

		public CdromFifo(int capacity) { _data = new byte[capacity]; }

		public int Size { get; private set; }

		public bool IsEmpty => Size == 0;
		public bool IsFull => Size >= _data.Length;

		public void Clear() { _head = 0; _tail = 0; Size = 0; }

		public void Push(byte b)
		{
			if (Size >= _data.Length) { _head = (_head + 1) & (_data.Length - 1); Size--; }
			_data[_tail] = b;
			_tail = (_tail + 1) & (_data.Length - 1);
			Size++;
		}

		public byte Pop()
		{
			if (Size == 0) return 0;
			byte v = _data[_head];
			_head = (_head + 1) & (_data.Length - 1);
			Size--;
			return v;
		}

		public byte Peek(int idx) => _data[(_head + idx) & (_data.Length - 1)];

		public void PushFrom(CdromFifo other)
		{
			while (!other.IsEmpty) Push(other.Pop());
		}

		public void PushRange(ReadOnlySpan<byte> src)
		{
			foreach (var b in src) Push(b);
		}

		public void SaveState(StateWriter w)
		{
			w.Bytes(_data);
			w.S32(_head); w.S32(_tail); w.S32(Size);
		}
		public void LoadState(StateReader r)
		{
			r.Bytes(_data);
			_head = r.S32(); _tail = r.S32(); Size = r.S32();
		}
	}

	private readonly Psx _psx;

	// Disc image
	private byte[] _disc;
	public bool HasDisc => _disc != null;

	// Timing events
	private readonly TimingEvent _commandEvent;
	private readonly TimingEvent _commandSecondResponseEvent;
	private readonly TimingEvent _asyncInterruptEvent;
	private readonly TimingEvent _driveEvent;

	// SBI replacement map (LBA -> 10-byte replacement subq); persisted from old impl
	private Dictionary<uint, byte[]> _sbiReplacement;
	public bool HasSbi => _sbiReplacement != null && _sbiReplacement.Count > 0;
	public int SbiReplacementCount => _sbiReplacement?.Count ?? 0;

	// Game serial / region: preserved from existing impl, BIOS uses
	private string _gameSerial;
	public string GameSerial => _gameSerial;
	private byte[] _regionId = "SCEA"u8.ToArray();

	// TOC
	private DiscTrack[] _tracks = Array.Empty<DiscTrack>();

	// Last interrupt tick for delay calculation
	private long _lastInterruptTime;

	// Command state
	private byte _command = 0xFF;
	private byte _commandSecondResponse = 0xFF;
	private DriveState _driveState = DriveState.Idle;

	// Status / mode / request registers
	private byte _statusIndex;          // bits 0-1
	private byte _secondaryStatus;
	private byte _modeBits;
	private byte _requestRegister;      // BFRD (bit 7), BFWR (bit 6), SMEN (bit 5)

	// Interrupt state
	private byte _interruptEnableRegister = InterruptRegisterMask;
	private byte _interruptFlagRegister;
	private byte _pendingAsyncInterrupt;

	// Seek/read state
	private bool _setlocPending;
	private bool _readAfterSeek;
	private bool _playAfterSeek;

	private byte _setlocMinute;
	private byte _setlocSecond;
	private byte _setlocFrame;

	private uint _requestedLba;
	private uint _currentLba;       // hold position
	private uint _currentSubqLba;   // disc position with respect to time
	private uint _seekStartLba;
	private uint _seekEndLba;
	private long _subqLbaUpdateTick;
	private uint _subqLbaUpdateCarry;

	// Audio / playback state
	private bool _muted;
	private bool _adpcmMuted;
	private bool _cddaAutoPausePending;
	private byte _cddaReportStartDelay;
	private byte _lastCddaReportFrameNibble = 0xFF;
	private byte _playTrackNumberBcd = 0xFF;
	private byte _asyncCommandParameter;
	private sbyte _fastForwardRate;

	// XA decoder state
	private byte _xaFilterFileNumber;
	private byte _xaFilterChannelNumber;
	private byte _xaCurrentFileNumber;
	private byte _xaCurrentChannelNumber;
	private bool _xaCurrentSet;
	private XaSubHeader _xaCurrentCodinginfo;
	private readonly int[] _xaLastSamples = new int[4];

	// XA filter tables
	private static readonly int[] XaFilterPos = { 0, 60, 115, 98, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
	private static readonly int[] XaFilterNeg = { 0, 0, -52, -55, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

	// Scratch buffer for decoded XA samples, preserved field name from old impl
	private readonly short[] _xaSampleBuf = new short[XaSamplesPerSector4Bit];

	// Sector header tracking
	private SubChannelQ _lastSubq;
	private SectorHeader _lastSectorHeader;
	private XaSubHeader _lastSectorSubheader;
	private bool _lastSectorHeaderValid;
	private bool _lastSubqNeedsUpdate;

	// FIFOs
	private readonly CdromFifo _paramFifo = new(ParamFifoSize);
	private readonly CdromFifo _responseFifo = new(ResponseFifoSize);
	private readonly CdromFifo _asyncResponseFifo = new(ResponseFifoSize);

	// Sector buffers
	private readonly SectorBuffer[] _sectorBuffers;
	private int _currentReadSectorBuffer;
	private int _currentWriteSectorBuffer;

	// Seek jitter PRNG
	private readonly Random _seekJitterRng = new(0x4B435544);

	// Diagnostic state (preserved from old impl)
	private byte _lastExecutedCmd;
	private int _diagDataSectors;
	private int _diagSbiHits;

	// ---- Save-state ----
	// Full drive/command/XA/FIFO/sector-buffer state + the four CDROM events.
	// EXCLUDED (restored by the still-mounted disc, not dynamic state): _disc,
	// _tracks, _gameSerial, _regionId, _sbiReplacement. Also excluded:
	// _seekJitterRng (PRNG: seek jitter is non-deterministic), _diag* counters,
	// and the static XA filter LUTs.
	public void SaveState(StateWriter w)
	{
		long g = _psx.Scheduler.GlobalTickCounter;
		w.S64(_lastInterruptTime);
		w.U8(_command); w.U8(_commandSecondResponse); w.U8((byte)_driveState);
		w.U8(_statusIndex); w.U8(_secondaryStatus); w.U8(_modeBits); w.U8(_requestRegister);
		w.U8(_interruptEnableRegister); w.U8(_interruptFlagRegister); w.U8(_pendingAsyncInterrupt);
		w.Bool(_setlocPending); w.Bool(_readAfterSeek); w.Bool(_playAfterSeek);
		w.U8(_setlocMinute); w.U8(_setlocSecond); w.U8(_setlocFrame);
		w.U32(_requestedLba); w.U32(_currentLba); w.U32(_currentSubqLba);
		w.U32(_seekStartLba); w.U32(_seekEndLba);
		w.S64(_subqLbaUpdateTick); w.U32(_subqLbaUpdateCarry);
		w.Bool(_muted); w.Bool(_adpcmMuted); w.Bool(_cddaAutoPausePending);
		w.U8(_cddaReportStartDelay); w.U8(_lastCddaReportFrameNibble);
		w.U8(_playTrackNumberBcd); w.U8(_asyncCommandParameter); w.U8((byte)_fastForwardRate);
		w.U8(_xaFilterFileNumber); w.U8(_xaFilterChannelNumber);
		w.U8(_xaCurrentFileNumber); w.U8(_xaCurrentChannelNumber); w.Bool(_xaCurrentSet);
		SaveXaSubHeader(w, _xaCurrentCodinginfo);
		w.Ints(_xaLastSamples);
		w.Shorts(_xaSampleBuf);
		w.U8(_lastSubq.TrackNumberBcd); w.U8(_lastSubq.IndexNumberBcd);
		w.U8(_lastSubq.RelativeMinuteBcd); w.U8(_lastSubq.RelativeSecondBcd); w.U8(_lastSubq.RelativeFrameBcd);
		w.U8(_lastSubq.AbsoluteMinuteBcd); w.U8(_lastSubq.AbsoluteSecondBcd); w.U8(_lastSubq.AbsoluteFrameBcd);
		w.U8(_lastSubq.ControlBits); w.Bool(_lastSubq.IsCrcValid);
		w.U8(_lastSectorHeader.Minute); w.U8(_lastSectorHeader.Second);
		w.U8(_lastSectorHeader.Frame); w.U8(_lastSectorHeader.SectorMode);
		SaveXaSubHeader(w, _lastSectorSubheader);
		w.Bool(_lastSectorHeaderValid); w.Bool(_lastSubqNeedsUpdate);
		_paramFifo.SaveState(w); _responseFifo.SaveState(w); _asyncResponseFifo.SaveState(w);
		foreach (var sb in _sectorBuffers) sb.SaveState(w);
		w.S32(_currentReadSectorBuffer); w.S32(_currentWriteSectorBuffer);
		w.U8(_lastExecutedCmd);
		_commandEvent.SaveState(w, g);
		_commandSecondResponseEvent.SaveState(w, g);
		_asyncInterruptEvent.SaveState(w, g);
		_driveEvent.SaveState(w, g);
	}

	public void LoadState(StateReader r)
	{
		long g = _psx.Scheduler.GlobalTickCounter;
		_lastInterruptTime = r.S64();
		_command = r.U8(); _commandSecondResponse = r.U8(); _driveState = (DriveState)r.U8();
		_statusIndex = r.U8(); _secondaryStatus = r.U8(); _modeBits = r.U8(); _requestRegister = r.U8();
		_interruptEnableRegister = r.U8(); _interruptFlagRegister = r.U8(); _pendingAsyncInterrupt = r.U8();
		_setlocPending = r.Bool(); _readAfterSeek = r.Bool(); _playAfterSeek = r.Bool();
		_setlocMinute = r.U8(); _setlocSecond = r.U8(); _setlocFrame = r.U8();
		_requestedLba = r.U32(); _currentLba = r.U32(); _currentSubqLba = r.U32();
		_seekStartLba = r.U32(); _seekEndLba = r.U32();
		_subqLbaUpdateTick = r.S64(); _subqLbaUpdateCarry = r.U32();
		_muted = r.Bool(); _adpcmMuted = r.Bool(); _cddaAutoPausePending = r.Bool();
		_cddaReportStartDelay = r.U8(); _lastCddaReportFrameNibble = r.U8();
		_playTrackNumberBcd = r.U8(); _asyncCommandParameter = r.U8(); _fastForwardRate = (sbyte)r.U8();
		_xaFilterFileNumber = r.U8(); _xaFilterChannelNumber = r.U8();
		_xaCurrentFileNumber = r.U8(); _xaCurrentChannelNumber = r.U8(); _xaCurrentSet = r.Bool();
		LoadXaSubHeader(r, ref _xaCurrentCodinginfo);
		r.Ints(_xaLastSamples);
		r.Shorts(_xaSampleBuf);
		_lastSubq.TrackNumberBcd = r.U8(); _lastSubq.IndexNumberBcd = r.U8();
		_lastSubq.RelativeMinuteBcd = r.U8(); _lastSubq.RelativeSecondBcd = r.U8(); _lastSubq.RelativeFrameBcd = r.U8();
		_lastSubq.AbsoluteMinuteBcd = r.U8(); _lastSubq.AbsoluteSecondBcd = r.U8(); _lastSubq.AbsoluteFrameBcd = r.U8();
		_lastSubq.ControlBits = r.U8(); _lastSubq.IsCrcValid = r.Bool();
		_lastSectorHeader.Minute = r.U8(); _lastSectorHeader.Second = r.U8();
		_lastSectorHeader.Frame = r.U8(); _lastSectorHeader.SectorMode = r.U8();
		LoadXaSubHeader(r, ref _lastSectorSubheader);
		_lastSectorHeaderValid = r.Bool(); _lastSubqNeedsUpdate = r.Bool();
		_paramFifo.LoadState(r); _responseFifo.LoadState(r); _asyncResponseFifo.LoadState(r);
		foreach (var sb in _sectorBuffers) sb.LoadState(r);
		_currentReadSectorBuffer = r.S32(); _currentWriteSectorBuffer = r.S32();
		_lastExecutedCmd = r.U8();
		_commandEvent.LoadState(r, g);
		_commandSecondResponseEvent.LoadState(r, g);
		_asyncInterruptEvent.LoadState(r, g);
		_driveEvent.LoadState(r, g);
	}

	private static void SaveXaSubHeader(StateWriter w, XaSubHeader h)
	{
		w.U8(h.FileNumber); w.U8(h.ChannelNumber); w.U8(h.SubmodeBits); w.U8(h.CodinginfoBits);
	}
	private static void LoadXaSubHeader(StateReader r, ref XaSubHeader h)
	{
		h.FileNumber = r.U8(); h.ChannelNumber = r.U8(); h.SubmodeBits = r.U8(); h.CodinginfoBits = r.U8();
	}

	// =====================================================================
	// Diagnostic properties (preserved from existing impl)
	// =====================================================================

	public byte DiagIFlags => _interruptFlagRegister;
	public byte DiagIEnable => _interruptEnableRegister;
	public bool DiagReading => _driveState == DriveState.Reading;
	public bool DiagSectorPending => _driveEvent != null && _driveEvent.IsActive &&
									 (_driveState == DriveState.Reading || _driveState == DriveState.Playing);
	public bool DiagHas2ndResponse => _commandSecondResponseEvent != null && _commandSecondResponseEvent.IsActive;
	public bool DiagCmdPending => _command != 0xFF;
	public byte DiagLastCmd => _lastExecutedCmd;
	public uint DiagSeekLba => _seekEndLba;
	public uint DiagLastLba => _currentLba;
	public string DiagRegion => System.Text.Encoding.ASCII.GetString(_regionId);
	public int DiagTrackCount => _tracks.Length;

	// =====================================================================
	// Construction
	// =====================================================================

	public PsxCdrom(Psx psx)
	{
		_psx = psx;
		_sectorBuffers = new SectorBuffer[NumSectorBuffers];
		
		for (int i = 0; i < NumSectorBuffers; i++)
			_sectorBuffers[i] = new SectorBuffer();

		_commandEvent = new TimingEvent(
			"CDROM Command Event", 1, 1,
			(p, t, l) => ((PsxCdrom)p).ExecuteCommand(l),
			this);

		_commandSecondResponseEvent = new TimingEvent(
			"CDROM Command Second Response Event", 1, 1,
			(p, t, l) => ((PsxCdrom)p).ExecuteCommandSecondResponse(),
			this);

		_asyncInterruptEvent = new TimingEvent(
			"CDROM Async Interrupt Event", InterruptDeliveryCycles, 1,
			(p, t, l) => ((PsxCdrom)p).DeliverAsyncInterrupt(),
			this);

		_driveEvent = new TimingEvent(
			"CDROM Drive Event", 1, 1,
			(p, t, l) => ((PsxCdrom)p).ExecuteDrive(l),
			this);
	}

	// =====================================================================
	// Reset
	// =====================================================================

	public void Reset()
	{
		_command = 0xFF;
		_commandEvent.Deactivate();
		ClearCommandSecondResponse();
		ClearDriveState();
		_statusIndex = 0;
		_secondaryStatus = 0;
		_secondaryStatus = (byte)(CanReadMedia() ? _secondaryStatus | StatMotorOn : _secondaryStatus & ~StatMotorOn);
		if (!CanReadMedia()) _secondaryStatus |= StatShellOpen;
		_modeBits = 0;
		_modeBits |= 0x20;
		_requestRegister = 0;
		_interruptEnableRegister = InterruptRegisterMask;
		_interruptFlagRegister = 0;
		_lastInterruptTime = _psx.Scheduler.GlobalTickCounter - MinimumInterruptDelay;
		ClearAsyncInterrupt();
		_setlocMinute = 0;
		_setlocSecond = 0;
		_setlocFrame = 0;
		_seekStartLba = 0;
		_seekEndLba = 0;
		_setlocPending = false;
		_readAfterSeek = false;
		_playAfterSeek = false;
		_muted = false;
		_adpcmMuted = false;
		_xaFilterFileNumber = 0;
		_xaFilterChannelNumber = 0;
		_xaCurrentFileNumber = 0;
		_xaCurrentChannelNumber = 0;
		_xaCurrentSet = false;
		_lastSectorHeader = default;
		_lastSectorSubheader = default;
		_lastSectorHeaderValid = false;
		_lastSubq = default;
		_cddaReportStartDelay = 0;
		_lastCddaReportFrameNibble = 0xFF;

		ClearSectorBuffers();
		ResetAudioDecoder();

		_paramFifo.Clear();
		_responseFifo.Clear();
		_asyncResponseFifo.Clear();

		_diagDataSectors = 0;
		_diagSbiHits = 0;

		UpdateStatusRegister();

		SetHoldPosition(0, 0);
	}

	/// <summary>Soft reset (called by Init command's QueueCommandSecondResponse). Returns ticks.</summary>
	private int SoftReset(int ticksLate)
	{
		bool wasDoubleSpeed = (_modeBits & 0x80) != 0;

		ClearCommandSecondResponse();
		ClearDriveState();
		_secondaryStatus = 0;
		if (CanReadMedia()) _secondaryStatus |= StatMotorOn;
		else _secondaryStatus |= StatShellOpen;
		_modeBits = 0;
		_modeBits |= 0x20;  // read_raw_sector default
		_requestRegister = 0;
		ClearAsyncInterrupt();
		_setlocMinute = 0;
		_setlocSecond = 0;
		_setlocFrame = 0;
		_setlocPending = false;
		_readAfterSeek = false;
		_playAfterSeek = false;
		_muted = false;
		_adpcmMuted = false;
		_cddaAutoPausePending = false;
		_cddaReportStartDelay = 0;
		_lastCddaReportFrameNibble = 0xFF;

		ClearSectorBuffers();
		ResetAudioDecoder();

		_paramFifo.Clear();
		_asyncResponseFifo.Clear();

		UpdateStatusRegister();

		int totalTicks;
		if (HasMedia())
		{
			int speedChangeTicks = wasDoubleSpeed ? GetTicksForSpeedChange() : 0;
			int seekTicks = (_currentLba != 0) ? GetTicksForSeek(0) : 0;
			totalTicks = Math.Max(speedChangeTicks + seekTicks, InitTicks) - ticksLate;

			if (_currentLba != 0)
			{
				_driveState = DriveState.SeekingImplicit;
				_driveEvent.SetIntervalAndSchedule(totalTicks);
				_requestedLba = 0;
				_seekStartLba = _currentLba;
				_seekEndLba = 0;
			}
			else
			{
				_driveState = DriveState.ChangingSpeedOrTOCRead;
				_driveEvent.Schedule(totalTicks);
			}
		}
		else
		{
			totalTicks = InitTicks - ticksLate;
		}
		return totalTicks;
	}

	// =====================================================================
	// LoadDisc / LoadSbi / DetectRegion / DetectGameSerial
	// =====================================================================

	public void LoadDisc(byte[] binData, DiscTrack[] tracks = null)
	{
		_disc = binData;
		// drive motor always spins on disc insertion
		_secondaryStatus = (byte)((_secondaryStatus & ~StatShellOpen) | StatMotorOn);

		if (binData.Length % RawSectorSize != 0)
			PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn,
				$"Disc image size {binData.Length} is not a multiple of {RawSectorSize}; " +
				$"trailing {binData.Length % RawSectorSize} bytes will be ignored.");

		_tracks = (tracks != null && tracks.Length > 0)
			? tracks
			: new[] { new DiscTrack(1, false, 0) };

		DetectRegion();
		int lastTrack = _tracks.Length > 0 ? _tracks[^1].Number : 1;
		PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Info,
			$"Disc loaded, {binData.Length / RawSectorSize} sectors, {lastTrack} track(s), region={System.Text.Encoding.ASCII.GetString(_regionId)}");
		foreach (var track in _tracks)
		{
			LbaToMsf(track.StartLba, out byte m, out byte s, out byte f);
			PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Info,
				$"Track {track.Number:D2} {(track.IsAudio ? "AUDIO" : "DATA")} start={m:X2}:{s:X2}:{f:X2} lba={track.StartLba} physical={track.PhysicalStartSector}");
		}

		DetectGameSerial();
	}

	public void LoadSbi(byte[] sbiData)
	{
		if (sbiData == null || sbiData.Length == 0)
		{
			_sbiReplacement = null;
			return;
		}
		_sbiReplacement = PsxSbi.Parse(sbiData);
	}

	private void DetectGameSerial()
	{
		_gameSerial = null;
		if (_disc == null) return;

		int maxSector = Math.Min(50, _disc.Length / RawSectorSize);
		for (int sec = 16; sec < maxSector; sec++)
		{
			int byteOffset = ResolveByteOffset((uint)sec);
			if (byteOffset < 0 || byteOffset + RawSectorSize > _disc.Length) continue;

			int dataStart = byteOffset + 24;
			int dataEnd = Math.Min(byteOffset + RawSectorSize, _disc.Length) - 12;
			for (int i = dataStart; i < dataEnd; i++)
			{
				if ((_disc[i] == 'S' || _disc[i] == 's') &&
					(_disc[i + 1] == 'L' || _disc[i + 1] == 'C') &&
					_disc[i + 4] == '_' && _disc[i + 8] == '.')
				{
					string prefix = System.Text.Encoding.ASCII.GetString(_disc, i, 4).ToUpperInvariant();
					if (prefix != "SLES" && prefix != "SLUS" && prefix != "SLPS" &&
						prefix != "SLPM" && prefix != "SCES" && prefix != "SCUS" &&
						prefix != "SCPS" && prefix != "SCPM" && prefix != "SLED" &&
						prefix != "SCED" && prefix != "SLEW")
						continue;

					bool valid = true;
					for (int j = 0; j < 11; j++)
					{
						if (j == 4 || j == 8) continue;
						byte c = _disc[i + j];
						if (j < 4)
						{
							if (!((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))) { valid = false; break; }
						}
						else
						{
							if (c < '0' || c > '9') { valid = false; break; }
						}
					}
					if (!valid) continue;

					_gameSerial = System.Text.Encoding.ASCII.GetString(_disc, i, 11).ToUpperInvariant();
					PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Info,
						$"Game serial detected: {_gameSerial}");
					return;
				}
			}
		}

		PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Info,
			"Game serial not found in ISO directory scan");
	}

	private void DetectRegion()
	{
		_regionId = "SCEA"u8.ToArray();
		if (_disc == null) return;

		int maxSector = Math.Min(50, _disc.Length / RawSectorSize);
		for (int lba = 16; lba < maxSector; lba++)
		{
			int byteOffset = ResolveByteOffset((uint)lba);
			if (byteOffset + RawSectorSize > _disc.Length) break;
			int sectorStart = byteOffset + 16;
			int sectorEnd = byteOffset + RawSectorSize - 4;
			for (int pos = sectorStart; pos < sectorEnd; pos++)
			{
				byte b0 = _disc[pos + 0];
				if (b0 != 'S') continue;
				byte b1 = _disc[pos + 1];
				byte b2 = _disc[pos + 2];
				byte b3 = _disc[pos + 3];
				if (b1 == 'L' && b2 == 'E' && (b3 == 'S' || b3 == 'W')) { SetRegion("SCEE"); return; }
				if (b1 == 'C' && b2 == 'E' && (b3 == 'S' || b3 == 'D')) { SetRegion("SCEE"); return; }
				if (b1 == 'L' && b2 == 'U' && b3 == 'S') { SetRegion("SCEA"); return; }
				if (b1 == 'C' && b2 == 'U' && b3 == 'S') { SetRegion("SCEA"); return; }
				if (b1 == 'L' && b2 == 'P' && (b3 == 'S' || b3 == 'M')) { SetRegion("SCEI"); return; }
				if (b1 == 'C' && b2 == 'P' && (b3 == 'S' || b3 == 'M')) { SetRegion("SCEI"); return; }
			}
		}
		PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn, "DetectRegion: no game ID found in sectors 16-50, defaulting to SCEA");
	}

	private void SetRegion(string r)
	{
		_regionId = [(byte)r[0], (byte)r[1], (byte)r[2], (byte)r[3]];
	}

	// =====================================================================
	// Helpers
	// =====================================================================

	private bool HasMedia() => _disc != null;
	private bool CanReadMedia() => _driveState != DriveState.ShellOpening && _disc != null;
	private bool IsMotorOn() => (_secondaryStatus & StatMotorOn) != 0;
	private bool IsSeeking() => _driveState is DriveState.SeekingLogical or DriveState.SeekingPhysical or DriveState.SeekingImplicit;
	private bool IsReading() => _driveState == DriveState.Reading;
	private bool IsReadingOrPlaying() => _driveState is DriveState.Reading or DriveState.Playing;
	private bool HasPendingCommand() => _command != 0xFF;
	private bool HasPendingInterrupt() => _interruptFlagRegister != 0;
	private bool HasPendingAsyncInterrupt() => _pendingAsyncInterrupt != 0;

	private DiscTrack FindTrackForLba(uint lba)
	{
		if (_tracks.Length == 0) return new DiscTrack(1, false, 0);
		DiscTrack result = _tracks[0];
		for (int i = 0; i < _tracks.Length; i++)
		{
			if (_tracks[i].StartLba <= lba) result = _tracks[i];
			else break;
		}
		return result;
	}

	private int ResolveByteOffset(uint lba)
	{
		DiscTrack track = FindTrackForLba(lba);
		uint sector = track.PhysicalStartSector + (lba >= track.StartLba ? lba - track.StartLba : 0);
		return (int)sector * RawSectorSize;
	}

	private static void LbaToMsf(uint lba, out byte m, out byte s, out byte f)
	{
		lba += 150;
		f = BinToBcd((byte)(lba % SingleSpeedSectorsPerSecond));
		lba /= SingleSpeedSectorsPerSecond;
		s = BinToBcd((byte)(lba % 60));
		m = BinToBcd((byte)(lba / 60));
	}

	private static byte BcdToBin(byte v) => (byte)((v >> 4) * 10 + (v & 0xF));
	private static byte BinToBcd(byte v) => (byte)(((v / 10) << 4) | (v % 10));

	private static bool IsValidPackedBcd(byte v)
	{
		return (v & 0x0F) <= 9 && ((v >> 4) & 0x0F) <= 9;
	}

	/// <summary>Build the live status byte (0x1F801800 read).</summary>
	private byte BuildStatus()
	{
		byte stat = 0;
		stat |= (byte)(_statusIndex & 3);
		// bit 2 = ADPBUSY = 0
		if (_paramFifo.IsEmpty) stat |= 0x08;
		if (!_paramFifo.IsFull) stat |= 0x10;
		if (!_responseFifo.IsEmpty) stat |= 0x20;
		// DRQSTS mirrors BFRD
		if ((_requestRegister & 0x80) != 0) stat |= 0x40;
		if (HasPendingCommand()) stat |= 0x80;
		return stat;
	}

	/// <summary>Recompute the DMA3 request line.
	/// DRQSTS = BFRD AND (current sector buffer has unread bytes).
	/// Asserting DMA3 when BFRD is set but the buffer is drained leads to the DMA controller
	/// reading garbage (in our impl ReadByte returns 0 when drained, that's stale bytes for the
	/// game). Gate on buffer state too.</summary>
	private void UpdateStatusRegister()
	{
		bool bfrd = (_requestRegister & 0x80) != 0;
		SectorBuffer sb = _sectorBuffers[_currentReadSectorBuffer];
		bool drqsts = bfrd && (sb.Position < sb.Size);
		_psx.Dma?.SetRequest(3, drqsts);
	}

	private void UpdateInterruptRequest()
	{
		if ((_interruptFlagRegister & _interruptEnableRegister) != 0)
			_psx.Interrupts.Raise(PsxConstants.IrqCdrom);
	}

	private void SetInterrupt(byte interruptType)
	{
		_interruptFlagRegister = (byte)(interruptType & InterruptRegisterMask);
		UpdateInterruptRequest();
	}

	private void SetAsyncInterrupt(byte interruptType)
	{
		// Don't fire same-type async if already unacked.
		if (_interruptFlagRegister == interruptType)
		{
			_asyncResponseFifo.Clear();
			return;
		}

		_pendingAsyncInterrupt = (byte)(interruptType & InterruptRegisterMask);
		if (!HasPendingInterrupt())
		{
			if (!HasPendingCommand())
				QueueDeliverAsyncInterrupt();
		}
	}

	private void ClearAsyncInterrupt()
	{
		_pendingAsyncInterrupt = 0;
		_asyncInterruptEvent.Deactivate();
		_asyncResponseFifo.Clear();
	}

	private void QueueDeliverAsyncInterrupt()
	{
		long diff = _psx.Scheduler.GlobalTickCounter - _lastInterruptTime;
		if (diff >= MinimumInterruptDelay)
		{
			DeliverAsyncInterrupt();
		}
		else
		{
			_asyncInterruptEvent.Schedule(InterruptDeliveryCycles);
		}
	}

	private void DeliverAsyncInterrupt()
	{
		if (HasPendingInterrupt())
		{
			// Race, reschedule for later.
			if (!_asyncInterruptEvent.IsActive)
				_asyncInterruptEvent.Schedule(InterruptDeliveryCycles);
			return;
		}

		_asyncInterruptEvent.Deactivate();
		if (_pendingAsyncInterrupt == 0) return;

		// Snap read sector buffer to write buffer on INT1 delivery (HC05 behavior).
		if (_pendingAsyncInterrupt == IntDataReady)
			_currentReadSectorBuffer = _currentWriteSectorBuffer;

		_responseFifo.Clear();
		_responseFifo.PushFrom(_asyncResponseFifo);
		_interruptFlagRegister = _pendingAsyncInterrupt;
		_pendingAsyncInterrupt = 0;
		UpdateInterruptRequest();
		UpdateStatusRegister();
		UpdateCommandEvent();
	}

	private void SendACKAndStat()
	{
		_responseFifo.Push(GetStat());
		SetInterrupt(IntAck);
	}

	private void SendErrorResponse(byte statBits = StatError, byte reason = ErrorReasonNotReady)
	{
		_responseFifo.Push((byte)(GetStat() | statBits));
		_responseFifo.Push(reason);
		SetInterrupt(IntError);
	}

	private void SendAsyncErrorResponse(byte statBits = StatError, byte reason = ErrorReasonNotReady)
	{
		_asyncResponseFifo.Push((byte)(GetStat() | statBits));
		_asyncResponseFifo.Push(reason);
		SetAsyncInterrupt(IntError);
	}

	private byte GetStat() => _secondaryStatus;

	// =====================================================================
	// MMIO Read/Write
	// =====================================================================

	public byte ReadByte(uint offset)
	{
		switch (offset)
		{
			case 0:
				return BuildStatus();

			case 1:
				return _responseFifo.IsEmpty ? (byte)0 : PopResponseAndUpdate();

			case 2:
				{
					SectorBuffer sb = _sectorBuffers[_currentReadSectorBuffer];
					bool bfrd = (_requestRegister & 0x80) != 0;
					byte value = 0;
					if (bfrd && sb.Position < sb.Size)
					{
						value = sb.Data[sb.Position++];
						CheckForSectorBufferReadComplete();
					}
					return value;
				}

			case 3:
				return (_statusIndex & 1) != 0
					? (byte)(_interruptFlagRegister | ~InterruptRegisterMask)
					: (byte)(_interruptEnableRegister | ~InterruptRegisterMask);

			default:
				return 0xFF;
		}
	}

	private byte PopResponseAndUpdate()
	{
		byte v = _responseFifo.Pop();
		UpdateStatusRegister();
		return v;
	}

	public void WriteByte(uint offset, byte value)
	{
		if (offset == 0)
		{
			_statusIndex = (byte)(value & 3);
			return;
		}

		uint reg = _statusIndex * 3u + (offset - 1u);
		switch (reg)
		{
			case 0:  // Command register
				BeginCommand(value);
				return;

			case 1:  // Parameter FIFO
				// When FIFO is full, the new value is silently DROPPED,
				// not replacing oldest. Our prior "RemoveOne + Push" behavior
				// would mangle multi-byte commands like SetLoc (3 params) if a stray write put us over capacity.
				if (!_paramFifo.IsFull)
					_paramFifo.Push(value);
				UpdateStatusRegister();
				return;

			case 2:  // Request register
				{
					_requestRegister = value;

					SectorBuffer sb = _sectorBuffers[_currentReadSectorBuffer];
					bool bfrd = (value & 0x80) != 0;
					if (!bfrd)
					{
						// "Clearing BFRD needs to reset the position of the current buffer.
						//  Metal Gear Solid: Special Missions (PAL) clears BFRD inbetween two DMAs
						//  during its disc detection, and needs the buffer to reset."
						sb.Position = 0;
					}

					UpdateStatusRegister();
					return;
				}

			case 3:  // Sound map data out (not implemented)
				return;

			case 4:  // Interrupt enable register
				_interruptEnableRegister = (byte)(value & InterruptRegisterMask);
				UpdateInterruptRequest();
				return;

			case 5:  // Interrupt flag register (writes-1-to-clear)
				{
					byte prevInterruptFlag = _interruptFlagRegister;
					_interruptFlagRegister &= (byte)(~value & InterruptRegisterMask);
					if (_interruptFlagRegister == 0)
					{
						if (prevInterruptFlag != 0)
							_lastInterruptTime = _psx.Scheduler.GlobalTickCounter;

						// Deassert CDROM IRQ line so a fresh peripheral pulse can fire a
						// new I_STAT bit-2 set. Without this our level-based IStat |= bit
						// would not see a rising edge on the next interrupt.
						_psx.Interrupts.Clear(PsxConstants.IrqCdrom);
						if (HasPendingAsyncInterrupt() && !HasPendingCommand())
							QueueDeliverAsyncInterrupt();
						else
							UpdateCommandEvent();
					}
					if ((value & 0x40) != 0)
					{
						_paramFifo.Clear();
						UpdateStatusRegister();
					}
					return;
				}

			case 6:  // Sound map coding info (not implemented)
				return;

			case 7:  // Audio volume L->L (not implemented as matrix; we don't apply matrix)
			case 8:  // L->R
			case 9:  // R->R
			case 10: // R->L
				return;

			case 11: // Audio volume apply changes
				{
					bool newAdpcmMuted = (value & 0x01) != 0;
					if (newAdpcmMuted != _adpcmMuted)
					{
						_psx.Spu?.SyncPendingSamples();
					}
					_adpcmMuted = newAdpcmMuted;
					return;
				}

			default:
				return;
		}
	}

	public uint ReadWord(uint offset) =>
		ReadByte(offset) | ((uint)ReadByte(offset + 1) << 8) |
		((uint)ReadByte(offset + 2) << 16) | ((uint)ReadByte(offset + 3) << 24);

	public void WriteWord(uint offset, uint value)
	{
		WriteByte(offset, (byte)value);
		WriteByte(offset + 1, (byte)(value >> 8));
		WriteByte(offset + 2, (byte)(value >> 16));
		WriteByte(offset + 3, (byte)(value >> 24));
	}

	// =====================================================================
	// Timing helpers
	// =====================================================================

	private int GetAckDelayForCommand(byte command)
	{
		if (command == 0x0A) return 80_000;
		return CanReadMedia() ? 25_000 : 15_000;
	}

	private int GetTicksForIDRead()
	{
		int ticks = IdReadTicks;
		if (_driveState == DriveState.SpinningUp)
			ticks += _driveEvent.GetTicksUntilNextExecution();
		return ticks;
	}

	private int GetTicksForRead()
	{
		int tps = PsxConstants.CpuHz;
		return (_modeBits & 0x80) != 0
			? tps / DoubleSpeedSectorsPerSecond
			: tps / SingleSpeedSectorsPerSecond;
	}

	private uint GetSectorsPerTrack(uint lba)
	{
		uint mm = lba / (75u * 60u);
		if (mm == 0) return 8;
		if (mm <= 4) return 9;
		if (mm <= 7) return 10;
		if (mm <= 11) return 11;
		if (mm <= 16) return 12;
		if (mm <= 23) return 13;
		if (mm <= 27) return 14;
		if (mm <= 32) return 15;
		if (mm <= 39) return 16;
		if (mm <= 44) return 17;
		if (mm <= 52) return 18;
		if (mm <= 60) return 19;
		if (mm <= 67) return 20;
		if (mm <= 74) return 21;
		return 22;
	}

	private int GetTicksForSeek(uint newLba, bool ignoreSpeedChange = false)
	{
		uint ticks = 0;

		// Update start position from current SubQ.
		if (IsSeeking()) UpdateSubQPositionWhileSeeking();
		else UpdateSubQPosition(false);

		uint currentLba = IsMotorOn() ? (IsSeeking() ? _seekEndLba : _currentSubqLba) : 0u;
		uint lbaDiff = newLba > currentLba ? newLba - currentLba : currentLba - newLba;

		if (!IsMotorOn())
		{
			ticks += (uint)((_driveState == DriveState.SpinningUp)
				? _driveEvent.GetTicksUntilNextExecution()
				: PsxConstants.CpuHz);
			if (_driveState == DriveState.ShellOpening || _driveState == DriveState.SpinningUp)
				ClearDriveState();
		}

		uint ticksPerSector = (uint)((_modeBits & 0x80) != 0
			? PsxConstants.CpuHz / DoubleSpeedSectorsPerSecond
			: PsxConstants.CpuHz / SingleSpeedSectorsPerSecond);
		uint sectorsPerTrack = GetSectorsPerTrack(currentLba);
		uint tjumpPosition = currentLba >= sectorsPerTrack ? currentLba - sectorsPerTrack : 0u;

		if (currentLba < newLba && lbaDiff <= sectorsPerTrack)
		{
			ticks += ticksPerSector * Math.Max(lbaDiff, 2u);
		}
		else if (currentLba >= newLba && tjumpPosition <= newLba)
		{
			ticks += ticksPerSector * Math.Max(newLba - tjumpPosition, 1u);
		}
		else if (lbaDiff < 7200)
		{
			uint switchPoint = (uint)(330.0 + (-63.1333 * Math.Log(Math.Clamp(currentLba / (75.0 * 60.0), 1.0, 72.0))));
			float seconds = lbaDiff < switchPoint ? 0.05f : 0.1f;
			ticks += (uint)(seconds * PsxConstants.CpuHz);
		}
		else
		{
			const float sledFixed = 0.05f;
			const float sledVariable = 0.9f - sledFixed;
			const float logWeight = 0.4f;
			const float maxSledLba = 72f * 60f * 75f;
			float seconds = sledFixed
				+ (sledVariable * (float)(Math.Log(lbaDiff) / Math.Log(maxSledLba))) * logWeight
				+ (sledVariable * (lbaDiff / maxSledLba)) * (1f - logWeight);
			ticks += (uint)(seconds * PsxConstants.CpuHz);
		}

		// Random 0.5-1ms jitter, critical for RE, Dino Crisis, Silent Hill timing loops.
		int jitterLo = PsxConstants.CpuHz / 2000;
		int jitterHi = PsxConstants.CpuHz / 1000;
		ticks += (uint)_seekJitterRng.Next(jitterLo, jitterHi + 1);

		if (_driveState == DriveState.ChangingSpeedOrTOCRead && !ignoreSpeedChange)
		{
			ticks += (uint)_driveEvent.GetTicksUntilNextExecution();
		}

		return (int)ticks;
	}

	private int GetTicksForPause()
	{
		if (!IsReadingOrPlaying()) return 27_000;

		uint sectorsPerTrack = GetSectorsPerTrack(_currentLba);
		int ticksPerRead = GetTicksForRead();
		int ticksToReachTarget = (int)(sectorsPerTrack - (uint)(IsReading() ? 2 : 0)) * ticksPerRead
								 - _driveEvent.GetTicksSinceLastExecution();
		int minTicks = (_modeBits & 0x80) != 0 ? 1_000_000 : 2_000_000;
		return Math.Max(ticksToReachTarget, minTicks);
	}

	private int GetTicksForStop(bool motorWasOn) =>
		motorWasOn ? ((_modeBits & 0x80) != 0 ? 25_000_000 : 13_000_000) : 7000;

	private int GetTicksForSpeedChange() =>
		(_modeBits & 0x80) != 0
			? (int)(0.6 * PsxConstants.CpuHz)
			: (int)(0.7 * PsxConstants.CpuHz);

	private int GetTicksForTOCRead() => HasMedia() ? PsxConstants.CpuHz / 2 : 0;

	private uint GetNextSectorToBeRead()
	{
		if (!IsReadingOrPlaying() && !IsSeeking()) return _currentLba;
		return _requestedLba;
	}

	// =====================================================================
	// Command dispatch
	// =====================================================================

	private void BeginCommand(byte command)
	{
		int ackDelay = GetAckDelayForCommand(command);
		if (HasPendingCommand())
		{
			// Heuristic: keep whichever command has more required parameters
			byte oldMin = GetCommandMinParameters(_command);
			byte newMin = GetCommandMinParameters(command);
			if (oldMin > newMin)
			{
				_paramFifo.Clear();
				return;
			}

			PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn,
				$"Cancelling pending command 0x{_command:X2} for new command 0x{command:X2}");

			if (_commandEvent.IsActive)
			{
				int elapsed = _commandEvent.Interval - _commandEvent.GetTicksUntilNextExecution();
				ackDelay = Math.Max(ackDelay - elapsed, 1);
				_commandEvent.Deactivate();

				if (HasPendingAsyncInterrupt())
				{
					QueueDeliverAsyncInterrupt();
				}
			}
		}

		_command = command;
		_commandEvent.SetIntervalAndSchedule(ackDelay);
		UpdateCommandEvent();
		UpdateStatusRegister();
	}

	private void EndCommand()
	{
		_paramFifo.Clear();
		_command = 0xFF;
		_commandEvent.Deactivate();
		UpdateStatusRegister();
	}

	private void UpdateCommandEvent()
	{
		if (!HasPendingCommand() || HasPendingInterrupt() || HasPendingAsyncInterrupt())
		{
			_commandEvent.Deactivate();
			return;
		}
		if (HasPendingCommand())
			_commandEvent.Activate();
	}

	private static byte GetCommandMinParameters(byte cmd)
	{
		return cmd switch
		{
			0x02 => 3,
			0x0D => 2,
			0x0E => 1,
			0x12 => 1,
			0x14 => 1,
			0x19 => 1,
			0x1D => 2,
			0x1F => 6,
			_ => 0,
		};
	}

	private static byte GetCommandMaxParameters(byte cmd)
	{
		return cmd switch
		{
			0x03 => 1,
			0x02 => 3,
			0x0D => 2,
			0x0E => 1,
			0x12 => 1,
			0x14 => 1,
			0x19 => 16,
			0x1D => 2,
			0x1F => 16,
			_ => 0,
		};
	}

	// =====================================================================
	// ExecuteCommand
	// =====================================================================

	private void ExecuteCommand(int ticksLate)
	{
		byte cmd = _command;
		byte minP = GetCommandMinParameters(cmd);
		byte maxP = GetCommandMaxParameters(cmd);
		if (_paramFifo.Size < minP || _paramFifo.Size > maxP)
		{
			SendErrorResponse(StatError, ErrorReasonIncorrectNumberOfParameters);
			EndCommand();
			return;
		}

		if (!_responseFifo.IsEmpty)
			_responseFifo.Clear();

		_commandEvent.Deactivate();
		_lastExecutedCmd = cmd;

		switch (cmd)
		{
			case 0x01:
				SendACKAndStat();
				if (CanReadMedia()) _secondaryStatus &= unchecked((byte)~StatShellOpen);
				EndCommand();
				return;

			case 0x19:
				{
					byte sub = _paramFifo.Pop();
					ExecuteTestCommand(sub);
					return;
				}

			case 0x1A:
				ClearCommandSecondResponse();
				if (!CanReadMedia()) SendErrorResponse();
				else
				{
					SendACKAndStat();
					QueueCommandSecondResponse(0x1A, GetTicksForIDRead());
				}
				EndCommand();
				return;

			case 0x1E:
				ClearCommandSecondResponse();
				if (!CanReadMedia()) SendErrorResponse();
				else
				{
					SendACKAndStat();
					SetHoldPosition(0, 0);
					QueueCommandSecondResponse(0x1E, GetTicksForTOCRead());
				}
				EndCommand();
				return;

			case 0x0D:
				{
					byte file = _paramFifo.Peek(0);
					byte channel = _paramFifo.Peek(1);
					_xaFilterFileNumber = file;
					_xaFilterChannelNumber = channel;
					_xaCurrentSet = false;
					PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Info,
						$"SetFilter file=0x{file:X2} channel=0x{channel:X2}");
					SendACKAndStat();
					EndCommand();
					return;
				}

			case 0x0E:
				{
					byte mode = _paramFifo.Peek(0);
					bool speedChange = ((mode ^ _modeBits) & 0x80) != 0;
					_modeBits = mode;
					PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn,
						$"SetMode 0x{_modeBits:X2} doubleSpeed={(_modeBits & 0x80) != 0} xa={(_modeBits & 0x40) != 0} raw={(_modeBits & 0x20) != 0} filter={(_modeBits & 0x08) != 0}");
					SendACKAndStat();
					EndCommand();

					if (speedChange)
					{
						if (_driveState == DriveState.ChangingSpeedOrTOCRead)
						{
							if (_driveEvent.GetTicksUntilNextExecution() >= (GetTicksForSpeedChange() / 4))
								ClearDriveState();
						}
						else if (_driveState != DriveState.SeekingImplicit && _driveState != DriveState.ShellOpening)
						{
							int changeTicks = GetTicksForSpeedChange();
							if (_driveState != DriveState.Idle)
							{
								// Delay current event by changeTicks
								int remaining = _driveEvent.GetTicksUntilNextExecution();
								_driveEvent.Schedule(remaining + changeTicks);
								if (IsReadingOrPlaying())
									_driveEvent.Interval = GetTicksForRead();
								PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn,
									$"Speed change while {_driveState}: delaying by {changeTicks} cycles");
							}
							else
							{
								_driveState = DriveState.ChangingSpeedOrTOCRead;
								_driveEvent.Schedule(changeTicks);
								PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn,
									$"Speed change while idle: {changeTicks} cycles");
							}
						}
					}
					return;
				}

			case 0x02:
				{
					byte mm = _paramFifo.Peek(0);
					byte ss = _paramFifo.Peek(1);
					byte ff = _paramFifo.Peek(2);
					if (((mm & 0x0F) > 0x09) || (mm > 0x99) || ((ss & 0x0F) > 0x09) || (ss >= 0x60) ||
						((ff & 0x0F) > 0x09) || (ff >= 0x75))
					{
						PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Error,
							$"Invalid SetLoc {mm:X2}:{ss:X2}:{ff:X2}");
						SendErrorResponse(StatError, ErrorReasonInvalidArgument);
					}
					else
					{
						SendACKAndStat();
						_setlocMinute = BcdToBin(mm);
						_setlocSecond = BcdToBin(ss);
						_setlocFrame = BcdToBin(ff);
						_setlocPending = true;
						PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Info,
							$"SetLoc MSF={mm:X2}:{ss:X2}:{ff:X2} => LBA={SetlocToLba()}");
					}
					EndCommand();
					return;
				}

			case 0x15:
			case 0x16:
				{
					bool logical = (cmd == 0x15);
					if (!CanReadMedia()) SendErrorResponse();
					else
					{
						SendACKAndStat();
						BeginSeeking(logical, false, false);
					}
					EndCommand();
					return;
				}

			case 0x12:
				{
					byte session = _paramFifo.Peek(0);
					if (!CanReadMedia() || _driveState == DriveState.Reading || _driveState == DriveState.Playing)
						SendErrorResponse();
					else if (session == 0)
						SendErrorResponse(StatError, ErrorReasonInvalidArgument);
					else
					{
						ClearCommandSecondResponse();
						SendACKAndStat();
						_asyncCommandParameter = session;
						_driveState = DriveState.ChangingSession;
						_driveEvent.Schedule(GetTicksForTOCRead());
					}
					EndCommand();
					return;
				}

			case 0x06:
			case 0x1B:
				if (!CanReadMedia())
					SendErrorResponse();
				else
				{
					SendACKAndStat();
					bool sameSetloc = !_setlocPending || SetlocToLba() == GetNextSectorToBeRead();
					if (sameSetloc &&
						(_driveState == DriveState.Reading ||
						 (IsSeeking() && _readAfterSeek)))
					{
						_setlocPending = false;
					}
					else
					{
						BeginReading();
					}
				}
				EndCommand();
				return;

			case 0x03:
				{
					byte track = _paramFifo.IsEmpty ? (byte)0 : BcdToBin(_paramFifo.Peek(0));
					if (!CanReadMedia()) SendErrorResponse();
					else
					{
						SendACKAndStat();
						if (track == 0 &&
							(!_setlocPending || SetlocToLba() == GetNextSectorToBeRead()) &&
							(_driveState == DriveState.Playing || (IsSeeking() && _playAfterSeek)))
						{
							_fastForwardRate = 0;
							_setlocPending = false;
						}
						else
						{
							BeginPlaying(track);
						}
					}
					EndCommand();
					return;
				}

			case 0x04:
				if (_driveState != DriveState.Playing || !CanReadMedia())
					SendErrorResponse();
				else
				{
					SendACKAndStat();
					if (_fastForwardRate < 0) _fastForwardRate = 0;
					_fastForwardRate = (sbyte)Math.Min(_fastForwardRate + 4, 12);
				}
				EndCommand();
				return;

			case 0x05:
				if (_driveState != DriveState.Playing || !CanReadMedia())
					SendErrorResponse();
				else
				{
					SendACKAndStat();
					if (_fastForwardRate > 0) _fastForwardRate = 0;
					_fastForwardRate = (sbyte)Math.Max(_fastForwardRate - 4, -12);
				}
				EndCommand();
				return;

			case 0x09:
				{
					int pauseTime = GetTicksForPause();
					if (IsReading() && _lastSubq.IsData)
					{
						uint spt = GetSectorsPerTrack(_currentLba);
						SetHoldPosition(_currentLba, spt <= _currentLba ? (_currentLba - spt) : 0);
					}
					ClearCommandSecondResponse();
					SendACKAndStat();

					if (_driveState == DriveState.SeekingLogical || _driveState == DriveState.SeekingPhysical ||
						((_driveState == DriveState.Reading || _driveState == DriveState.Playing) &&
						 (_secondaryStatus & StatSeeking) != 0))
					{
						PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn,
							"Pause while seeking -> error");
						SendErrorResponse();
						EndCommand();
						return;
					}

					ClearAsyncInterrupt();
					_driveState = DriveState.Idle;
					_driveEvent.Deactivate();
					ClearActiveStatBits();
					ResetAudioDecoder();
					QueueCommandSecondResponse(0x09, pauseTime);
					EndCommand();
					return;
				}

			case 0x08:
				{
					int stopTime = GetTicksForStop(IsMotorOn());
					ClearAsyncInterrupt();
					ClearCommandSecondResponse();
					SendACKAndStat();
					StopMotor();
					QueueCommandSecondResponse(0x08, stopTime);
					EndCommand();
					return;
				}

			case 0x0A:
				{
					if (_commandSecondResponse == 0x0A)
					{
						// Still pending, just ack.
						EndCommand();
						return;
					}
					SendACKAndStat();
					int resetTicks = SoftReset(ticksLate);
					QueueCommandSecondResponse(0x0A, resetTicks);
					EndCommand();
					return;
				}

			case 0x07:
				if (IsMotorOn()) SendErrorResponse(StatError, ErrorReasonIncorrectNumberOfParameters);
				else if (!CanReadMedia()) SendErrorResponse();
				else
				{
					SendACKAndStat();
					if (_commandSecondResponse == 0x07) { EndCommand(); return; }
					_secondaryStatus |= StatMotorOn;
					StartMotor();
					QueueCommandSecondResponse(0x07, MotorOnResponseTicks);
				}
				EndCommand();
				return;

			case 0x0B:
				_muted = true;
				SendACKAndStat();
				EndCommand();
				return;

			case 0x0C:
				_muted = false;
				SendACKAndStat();
				EndCommand();
				return;

			case 0x10:
				if (!_lastSectorHeaderValid)
					SendErrorResponse();
				else
				{
					UpdateSubQPosition(true);
					// _lastSectorHeader fields are raw disc bytes (already BCD).
					_responseFifo.Push(_lastSectorHeader.Minute);
					_responseFifo.Push(_lastSectorHeader.Second);
					_responseFifo.Push(_lastSectorHeader.Frame);
					_responseFifo.Push(_lastSectorHeader.SectorMode);
					_responseFifo.Push(_lastSectorSubheader.FileNumber);
					_responseFifo.Push(_lastSectorSubheader.ChannelNumber);
					_responseFifo.Push(_lastSectorSubheader.SubmodeBits);
					_responseFifo.Push(_lastSectorSubheader.CodinginfoBits);
					SetInterrupt(IntAck);
				}
				EndCommand();
				return;

			case 0x11:
				if (!CanReadMedia()) SendErrorResponse();
				else
				{
					if (IsSeeking()) UpdateSubQPositionWhileSeeking();
					else UpdateSubQPosition(false);
					EnsureLastSubQValid();
					_responseFifo.Push(_lastSubq.TrackNumberBcd);
					_responseFifo.Push(_lastSubq.IndexNumberBcd);
					_responseFifo.Push(_lastSubq.RelativeMinuteBcd);
					_responseFifo.Push(_lastSubq.RelativeSecondBcd);
					_responseFifo.Push(_lastSubq.RelativeFrameBcd);
					_responseFifo.Push(_lastSubq.AbsoluteMinuteBcd);
					_responseFifo.Push(_lastSubq.AbsoluteSecondBcd);
					_responseFifo.Push(_lastSubq.AbsoluteFrameBcd);
					SetInterrupt(IntAck);
				}
				EndCommand();
				return;

			case 0x13:
				if (CanReadMedia())
				{
					_responseFifo.Push(GetStat());
					_responseFifo.Push(BinToBcd(_tracks.Length > 0 ? _tracks[0].Number : (byte)1));
					_responseFifo.Push(BinToBcd(_tracks.Length > 0 ? _tracks[^1].Number : (byte)1));
					SetInterrupt(IntAck);
				}
				else SendErrorResponse();
				EndCommand();
				return;

			case 0x14:
				{
					if (!CanReadMedia()) { SendErrorResponse(); EndCommand(); return; }
					byte trackBcd = _paramFifo.Peek(0);
					if (!IsValidPackedBcd(trackBcd)) { SendErrorResponse(StatError, ErrorReasonInvalidArgument); EndCommand(); return; }
					byte trackNum = BcdToBin(trackBcd);
					_responseFifo.Push(GetStat());
					if (trackNum == 0)
					{
						uint discSectors = (uint)(_disc.Length / RawSectorSize);
						LbaToMsf(discSectors, out byte m, out byte s, out _);
						_responseFifo.Push(m);
						_responseFifo.Push(s);
					}
					else
					{
						DiscTrack? found = null;
						foreach (var t in _tracks) if (t.Number == trackNum) { found = t; break; }
						if (found.HasValue)
						{
							LbaToMsf(found.Value.StartLba, out byte m, out byte s, out _);
							_responseFifo.Push(m);
							_responseFifo.Push(s);
						}
						else
						{
							_responseFifo.Push(BinToBcd(0));
							_responseFifo.Push(BinToBcd(2));
						}
					}
					SetInterrupt(IntAck);
					EndCommand();
					return;
				}

			case 0x0F:
				_responseFifo.Push(GetStat());
				_responseFifo.Push(_modeBits);
				_responseFifo.Push(0);
				_responseFifo.Push(_xaFilterFileNumber);
				_responseFifo.Push(_xaFilterChannelNumber);
				SetInterrupt(IntAck);
				EndCommand();
				return;

			case 0x00:
				SendErrorResponse(StatError, ErrorReasonInvalidCommand);
				EndCommand();
				return;

			case 0x1F:
				SendErrorResponse(StatError, ErrorReasonInvalidCommand);
				_command = 0xFF;
				_commandEvent.Deactivate();
				UpdateStatusRegister();
				return;

			default:
				PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Error,
					$"UNIMPLEMENTED cmd 0x{cmd:X2} from PC=0x{_psx.Cpu.Pc:X8} RA=0x{_psx.Cpu.Gpr[31]:X8}");
				SendErrorResponse(StatError, ErrorReasonInvalidCommand);
				EndCommand();
				return;
		}
	}

	private uint SetlocToLba()
	{
		uint lba = (((uint)_setlocMinute) * 60u + _setlocSecond) * SingleSpeedSectorsPerSecond + _setlocFrame;
		return lba >= 150 ? lba - 150 : 0;
	}

	private void ExecuteTestCommand(byte subcommand)
	{
		switch (subcommand)
		{
			case 0x04:  // Reset SCEx counters
				_secondaryStatus |= StatMotorOn;
				_responseFifo.Push(GetStat());
				SetInterrupt(IntAck);
				EndCommand();
				return;

			case 0x05:  // Read SCEx counters
				_responseFifo.Push(GetStat());
				_responseFifo.Push(0);
				_responseFifo.Push(0);
				SetInterrupt(IntAck);
				EndCommand();
				return;

			case 0x20:  // Get CDROM BIOS Date/Version, hardcoded to PU-18 us/eur 1997-01-10
				_responseFifo.Push(0x97);
				_responseFifo.Push(0x01);
				_responseFifo.Push(0x10);
				_responseFifo.Push(0xC2);
				SetInterrupt(IntAck);
				EndCommand();
				return;

			case 0x22:  // Get CDROM region ID string
				{
					byte[] resp = "for U/C"u8.ToArray();
					_responseFifo.PushRange(resp);
					SetInterrupt(IntAck);
					EndCommand();
					return;
				}

			case 0x60:  // Read memory, returns 0
				if (_paramFifo.Size < 2)
				{
					SendErrorResponse(StatError, ErrorReasonIncorrectNumberOfParameters);
					EndCommand();
					return;
				}
				_responseFifo.Push(0x00);
				SetInterrupt(IntAck);
				EndCommand();
				return;

			default:
				PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Error,
					$"Unknown test command 0x{subcommand:X2}");
				SendErrorResponse(StatError, ErrorReasonInvalidCommand);
				EndCommand();
				return;
		}
	}

	private void ExecuteCommandSecondResponse()
	{
		switch (_commandSecondResponse)
		{
			case 0x1A:
				DoIDRead();
				break;

			case 0x1E:
			case 0x07:
			case 0x08:
			case 0x09:
				DoStatSecondResponse();
				break;
			
			case 0x0A:
				if (HasPendingCommand()) EndCommand();
				DoStatSecondResponse();
				break;
		}

		_commandSecondResponse = 0xFF;
		_commandSecondResponseEvent.Deactivate();
	}

	private void QueueCommandSecondResponse(byte command, int ticks)
	{
		ClearCommandSecondResponse();
		_commandSecondResponse = command;
		_commandSecondResponseEvent.Schedule(ticks);
	}

	private void ClearCommandSecondResponse()
	{
		_commandSecondResponseEvent.Deactivate();
		_commandSecondResponse = 0xFF;
	}

	// =====================================================================
	// Drive event dispatch
	// =====================================================================

	private void ExecuteDrive(int ticksLate)
	{
		switch (_driveState)
		{
			case DriveState.ShellOpening:
				DoShellOpenComplete();
				break;
			case DriveState.SeekingPhysical:
			case DriveState.SeekingLogical:
				DoSeekComplete(ticksLate);
				break;
			case DriveState.SeekingImplicit:
				CompleteSeek();
				break;
			case DriveState.Reading:
			case DriveState.Playing:
				DoSectorRead();
				break;
			case DriveState.ChangingSession:
				DoChangeSessionComplete();
				break;
			case DriveState.SpinningUp:
				DoSpinUpComplete();
				break;
			case DriveState.ChangingSpeedOrTOCRead:
				DoSpeedChangeOrImplicitTOCReadComplete();
				break;
			case DriveState.Idle:
			default:
				break;
		}
	}

	private void ClearDriveState()
	{
		_driveState = DriveState.Idle;
		_driveEvent.Deactivate();
	}

	private void ClearActiveStatBits()
	{
		_secondaryStatus &= unchecked((byte)~(StatSeeking | StatReading | StatPlayingCdda));
	}

	private void SetSeekingBits()
	{
		_secondaryStatus = (byte)((_secondaryStatus & ~(StatReading | StatPlayingCdda)) | StatMotorOn | StatSeeking);
	}

	private void SetReadingBits(bool audio)
	{
		_secondaryStatus = (byte)(_secondaryStatus & ~(StatSeeking | StatReading | StatPlayingCdda));
		_secondaryStatus |= audio ? (byte)(StatMotorOn | StatPlayingCdda) : (byte)(StatMotorOn | StatReading);
	}

	private void DoShellOpenComplete()
	{
		ClearDriveState();
		if (CanReadMedia()) StartMotor();
	}

	private bool CompleteSeek()
	{
		bool logical = (_driveState == DriveState.SeekingLogical);
		ClearDriveState();

		// Without a real CDImage reader we synthesize SubQ from the requested LBA.
		uint targetLba = _requestedLba;
		_currentSubqLba = targetLba;
		_lastSubqNeedsUpdate = false;
		_subqLbaUpdateTick = _psx.Scheduler.GlobalTickCounter;
		_subqLbaUpdateCarry = 0;
		_currentLba = targetLba;

		SubChannelQ synthesized = GetSectorSubQ(targetLba);
		bool seekOkay = true;
		if (synthesized.IsCrcValid)
		{
			_lastSubq = synthesized;
			_lastSubqNeedsUpdate = false;

			// Read raw sector header for logical seek
			if (logical && synthesized.IsData)
			{
				int rawOff = ResolveByteOffset(targetLba);
				if (rawOff >= 0 && rawOff + RawSectorSize <= (_disc?.Length ?? 0))
				{
					ProcessDataSectorHeader(rawOff);
					LbaToMsf(targetLba, out byte mm, out byte ss, out byte ff);
					// BCD vs BCD: LbaToMsf returns BCD; _lastSectorHeader.{Minute,Second,Frame}
					// are raw disc bytes (also BCD).
					seekOkay = (_lastSectorHeader.Minute == mm &&
								_lastSectorHeader.Second == ss &&
								_lastSectorHeader.Frame == ff);

					if (seekOkay && !_playAfterSeek && !_readAfterSeek)
					{
						// Pull SubQ back by 2 frames (the data sector header was found 2 sectors before target).
						_currentSubqLba = _currentLba >= SubqSectorSkew ? _currentLba - SubqSectorSkew : 0u;
						_lastSubqNeedsUpdate = true;
					}
				}
			}
			else if (logical && !synthesized.IsData)
			{
				if (_readAfterSeek)
					seekOkay = (_modeBits & 0x01) != 0; // mode.cdda required for audio reads
			}
		}
		return seekOkay;
	}

	private void DoSeekComplete(int ticksLate)
	{
		bool seekOkay = CompleteSeek();
		
		if (seekOkay)
		{
			if (_readAfterSeek)
			{
				BeginReading(ticksLate, true);
			}
			else if (_playAfterSeek)
			{
				BeginPlaying(0, ticksLate, true);
			}
			else
			{
				ClearActiveStatBits();
				_asyncResponseFifo.Push(GetStat());
				SetAsyncInterrupt(IntComplete);
			}
		}
		else
		{
			PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn, $"Seek to LBA {_seekEndLba} failed");
			ClearActiveStatBits();
			SendAsyncErrorResponse(StatSeekError, 0x04);
			_lastSectorHeaderValid = false;
		}

		_setlocPending = false;
		_readAfterSeek = false;
		_playAfterSeek = false;
		UpdateStatusRegister();
	}

	private void DoStatSecondResponse()
	{
		if (!CanReadMedia())
		{
			SendAsyncErrorResponse(StatError, 0x08);
			return;
		}
		_asyncResponseFifo.Clear();
		_asyncResponseFifo.Push(GetStat());
		SetAsyncInterrupt(IntComplete);
	}

	private void DoChangeSessionComplete()
	{
		ClearDriveState();
		ClearActiveStatBits();
		_secondaryStatus |= StatMotorOn;
		_asyncResponseFifo.Clear();
		if (_asyncCommandParameter == 0x01)
		{
			_asyncResponseFifo.Push(GetStat());
			SetAsyncInterrupt(IntComplete);
		}
		else
		{
			SendAsyncErrorResponse(StatSeekError, 0x40);
		}
	}

	private void DoSpinUpComplete()
	{
		_driveState = DriveState.Idle;
		_driveEvent.Deactivate();
		ClearActiveStatBits();
		_secondaryStatus |= StatMotorOn;
	}

	private void DoSpeedChangeOrImplicitTOCReadComplete()
	{
		_driveState = DriveState.Idle;
		_driveEvent.Deactivate();
	}

	private void DoIDRead()
	{
		ClearActiveStatBits();
		_secondaryStatus = (byte)((_secondaryStatus & ~StatMotorOn) | (CanReadMedia() ? StatMotorOn : 0));

		byte statByte = GetStat();
		byte flagsByte = 0;
		if (!CanReadMedia())
		{
			statByte |= StatIdError;
			flagsByte |= 1 << 6; // Disc Missing
		}
		// Region check is permissive (always allowed); no unlicensed/wrong-region branches.

		_asyncResponseFifo.Clear();
		_asyncResponseFifo.Push(statByte);
		_asyncResponseFifo.Push(flagsByte);
		_asyncResponseFifo.Push(0x20); // Disc type: data
		_asyncResponseFifo.Push(0x00);
		_asyncResponseFifo.Push(_regionId[0]);
		_asyncResponseFifo.Push(_regionId[1]);
		_asyncResponseFifo.Push(_regionId[2]);
		_asyncResponseFifo.Push(_regionId[3]);

		SetAsyncInterrupt(flagsByte != 0 ? IntError : IntComplete);
	}

	// =====================================================================
	// Reading / Playing / Seeking
	// =====================================================================

	private void BeginReading(int ticksLate = 0, bool afterSeek = false)
	{
		if (!afterSeek && _setlocPending)
		{
			BeginSeeking(true, true, false);
			return;
		}

		if (IsSeeking())
		{
			if (_driveState == DriveState.SeekingImplicit)
				_driveState = DriveState.SeekingLogical;
			_readAfterSeek = true;
			_playAfterSeek = false;
			return;
		}

		int ticks = GetTicksForRead();
		int firstSectorTicks = ticks + (afterSeek ? 0 : GetTicksForSeek(_currentLba)) - ticksLate;

		ClearCommandSecondResponse();
		ClearAsyncInterrupt();
		ClearSectorBuffers();
		ResetAudioDecoder();

		if (!afterSeek) SetSeekingBits();

		_driveState = DriveState.Reading;
		_driveEvent.Interval = ticks;
		_driveEvent.Schedule(firstSectorTicks);

		_requestedLba = _currentLba;
		_seekStartLba = 0;
		_seekEndLba = 0;
	}

	private void BeginPlaying(byte track, int ticksLate = 0, bool afterSeek = false)
	{
		_playTrackNumberBcd = track;
		_fastForwardRate = 0;

		if (track != 0)
		{
			byte trackBin = track;
			DiscTrack? found = null;
			foreach (var t in _tracks) if (t.Number == trackBin) { found = t; break; }
			if (found.HasValue)
			{
				LbaToMsf(found.Value.StartLba, out byte m, out byte s, out byte f);
				_setlocMinute = BcdToBin(m);
				_setlocSecond = BcdToBin(s);
				_setlocFrame = BcdToBin(f);
				_setlocPending = true;
			}
		}

		if (_setlocPending)
		{
			BeginSeeking(false, false, true);
			return;
		}

		int ticks = GetTicksForRead();
		int firstSectorTicks = ticks + (afterSeek ? 0 : GetTicksForSeek(_currentLba, true)) - ticksLate;

		ClearCommandSecondResponse();
		ClearAsyncInterrupt();
		ClearSectorBuffers();
		ResetAudioDecoder();

		_cddaReportStartDelay = CddaReportStartDelay;
		_lastCddaReportFrameNibble = 0xFF;

		_driveState = DriveState.Playing;
		_driveEvent.Interval = ticks;
		_driveEvent.Schedule(firstSectorTicks);

		_requestedLba = _currentLba;
	}

	private void BeginSeeking(bool logical, bool readAfterSeek, bool playAfterSeek)
	{
		_readAfterSeek = readAfterSeek;
		_playAfterSeek = playAfterSeek;
		_setlocPending = false;

		uint seekLba = SetlocToLba();
		int seekTime;
		if (logical && !readAfterSeek && _currentSubqLba == (seekLba - SubqSectorSkew) && _seekEndLba == seekLba &&
			(_psx.Scheduler.GlobalTickCounter - _subqLbaUpdateTick) < GetTicksForRead())
		{
			seekTime = MinSeekTicks;
		}
		else
		{
			seekTime = GetTicksForSeek(seekLba, playAfterSeek);
		}

		ClearCommandSecondResponse();
		ClearAsyncInterrupt();
		ClearSectorBuffers();
		ResetAudioDecoder();

		SetSeekingBits();
		_lastSectorHeaderValid = false;

		_driveState = logical ? DriveState.SeekingLogical : DriveState.SeekingPhysical;
		_driveEvent.SetIntervalAndSchedule(seekTime);

		_seekStartLba = _currentLba;
		_seekEndLba = seekLba;
		_requestedLba = seekLba;
	}

	private void StartMotor()
	{
		if (_driveState == DriveState.SpinningUp)
			return;
		
		_driveState = DriveState.SpinningUp;
		_driveEvent.Schedule(PsxConstants.CpuHz);
	}

	private void StopMotor()
	{
		ClearActiveStatBits();
		_secondaryStatus = (byte)(_secondaryStatus & ~StatMotorOn);
		ClearDriveState();
		SetHoldPosition(0, 0);
		_lastSectorHeaderValid = false;
	}

	private void StopReadingWithDataEnd()
	{
		ClearAsyncInterrupt();
		_asyncResponseFifo.Push(GetStat());
		SetAsyncInterrupt(IntDataEnd);
		ClearActiveStatBits();
		ClearDriveState();
	}

	private void StopReadingWithError(byte reason = ErrorReasonNotReady)
	{
		ClearAsyncInterrupt();
		SendAsyncErrorResponse(StatError, reason);
		ClearActiveStatBits();
		ClearDriveState();
	}

	private void SetHoldPosition(uint lba, uint subqLba)
	{
		_lastSubqNeedsUpdate |= (_currentSubqLba != subqLba);
		_currentLba = lba;
		_currentSubqLba = subqLba;
		_subqLbaUpdateTick = _psx.Scheduler.GlobalTickCounter;
		_subqLbaUpdateCarry = 0;
	}

	private void EnsureLastSubQValid()
	{
		if (!_lastSubqNeedsUpdate) return;
		_lastSubqNeedsUpdate = false;
		SubChannelQ subq = GetSectorSubQ(_currentSubqLba);
		if (subq.IsCrcValid) _lastSubq = subq;
	}

	private void UpdateSubQPositionWhileSeeking()
	{
		float completedFrac = 1.0f - Math.Min(_driveEvent.GetTicksUntilNextExecution() / Math.Max(1, (float)_driveEvent.Interval), 1.0f);

		uint currentLba;
		if (_seekEndLba > _seekStartLba)
		{
			uint delta = (uint)Math.Max(1, (int)((_seekEndLba - _seekStartLba) * completedFrac));
			currentLba = _seekStartLba + delta;
		}
		else if (_seekEndLba < _seekStartLba)
		{
			uint delta = (uint)Math.Max(1, (int)((_seekStartLba - _seekEndLba) * completedFrac));
			currentLba = _seekStartLba - delta;
		}
		else return;

		_lastSubqNeedsUpdate = _currentSubqLba != currentLba;
		_currentSubqLba = currentLba;
		_subqLbaUpdateTick = _psx.Scheduler.GlobalTickCounter;
		_subqLbaUpdateCarry = 0;
	}

	private void UpdateSubQPosition(bool updateLogical)
	{
		long ticks = _psx.Scheduler.GlobalTickCounter;
		if (IsSeeking() || IsReadingOrPlaying() || !IsMotorOn())
		{
			if ((_secondaryStatus & (StatReading | StatPlayingCdda | StatMotorOn)) == StatMotorOn &&
				_currentLba != _currentSubqLba)
			{
				SetHoldPosition(_currentLba, _currentLba);
			}
			return;
		}

		uint ticksPerRead = (uint)GetTicksForRead();
		uint diff = (uint)((ticks - _subqLbaUpdateTick) + _subqLbaUpdateCarry);
		uint sectorDiff = diff / ticksPerRead;
		uint carry = diff % ticksPerRead;
		if (sectorDiff == 0) return;

		uint holdOffset = _lastSectorHeaderValid ? 2u : 0u;
		uint sectorsPerTrack = GetSectorsPerTrack(_currentLba);
		uint holdPosition = _currentLba + holdOffset;
		uint tjumpPosition = holdPosition >= sectorsPerTrack ? holdPosition - sectorsPerTrack : 0u;
		uint oldOffset = _currentSubqLba - tjumpPosition;
		uint newOffset = (oldOffset + sectorDiff) % sectorsPerTrack;
		uint newSubqLba = tjumpPosition + newOffset;

		if (_currentSubqLba != newSubqLba)
		{
			_currentSubqLba = newSubqLba;
			_lastSubqNeedsUpdate = true;
			_subqLbaUpdateTick = ticks;
			_subqLbaUpdateCarry = carry;

			if (updateLogical)
			{
				SubChannelQ subq = GetSectorSubQ(newSubqLba);
				if (subq.IsCrcValid)
				{
					_lastSubq = subq;
					_lastSubqNeedsUpdate = false;
				}
				int rawOff = ResolveByteOffset(newSubqLba);
				if (rawOff >= 0 && rawOff + RawSectorSize <= (_disc?.Length ?? 0))
					ProcessDataSectorHeader(rawOff);
			}
		}
	}

	// =====================================================================
	// Sector read pipeline
	// =====================================================================

	private void DoSectorRead()
	{
		if (_disc == null) { StopReadingWithError(); return; }

		_currentLba = _requestedLba;
		_currentSubqLba = _currentLba;
		_lastSubqNeedsUpdate = false;
		_subqLbaUpdateTick = _psx.Scheduler.GlobalTickCounter;
		_subqLbaUpdateCarry = 0;

		SetReadingBits(_driveState == DriveState.Playing);

		SubChannelQ subq = GetSectorSubQ(_currentLba);
		bool subqValid = subq.IsCrcValid;
		if (subqValid) _lastSubq = subq;

		int rawOff = ResolveByteOffset(_currentLba);
		if (rawOff < 0 || rawOff + RawSectorSize > _disc.Length)
		{
			StopReadingWithDataEnd();
			StopMotor();
			return;
		}

		bool isDataSector = subq.IsData;
		if (isDataSector)
		{
			ProcessDataSectorHeader(rawOff);
		}
		else if ((_modeBits & 0x02) != 0)  // auto_pause
		{
			if (_cddaAutoPausePending)
			{
				_cddaAutoPausePending = false;
				StopReadingWithDataEnd();
				return;
			}
			if (_playTrackNumberBcd == 0)
				_playTrackNumberBcd = subq.TrackNumberBcd;
			else if (_playTrackNumberBcd != subq.TrackNumberBcd)
				_cddaAutoPausePending = true;
		}

		uint nextSector = _currentLba + 1u;
		if (isDataSector && _driveState == DriveState.Reading)
		{
			ProcessDataSector(rawOff);
		}
		else if (!isDataSector && (_driveState == DriveState.Playing ||
			(_driveState == DriveState.Reading && (_modeBits & 0x01) != 0)))
		{
			ProcessCDDASector(rawOff, subq, subqValid);
			if (_fastForwardRate != 0)
				nextSector = (uint)((int)_currentLba + _fastForwardRate);
		}

		_requestedLba = nextSector;
	}

	private void ProcessDataSectorHeader(int rawOff)
	{
		if (_disc == null || rawOff + 24 > _disc.Length) return;
		_lastSectorHeader.Minute = _disc[rawOff + SectorSyncSize + 0];
		_lastSectorHeader.Second = _disc[rawOff + SectorSyncSize + 1];
		_lastSectorHeader.Frame = _disc[rawOff + SectorSyncSize + 2];
		_lastSectorHeader.SectorMode = _disc[rawOff + SectorSyncSize + 3];
		_lastSectorSubheader.FileNumber = _disc[rawOff + SectorSyncSize + 4];
		_lastSectorSubheader.ChannelNumber = _disc[rawOff + SectorSyncSize + 5];
		_lastSectorSubheader.SubmodeBits = _disc[rawOff + SectorSyncSize + 6];
		_lastSectorSubheader.CodinginfoBits = _disc[rawOff + SectorSyncSize + 7];
		_lastSectorHeaderValid = true;
	}

	private void ProcessDataSector(int rawOff)
	{
		// XA realtime audio -> decode to SPU, no CPU INT1
		if ((_modeBits & 0x40) != 0 && _lastSectorHeader.SectorMode == 2)
		{
			if (_lastSectorSubheader.SubmodeRealtime && _lastSectorSubheader.SubmodeAudio)
			{
				ProcessXAADPCMSector(rawOff);
				return;
			}
		}

		int sbNum = (_currentWriteSectorBuffer + 1) % NumSectorBuffers;
		SectorBuffer sb = _sectorBuffers[sbNum];

		bool readRaw = (_modeBits & 0x20) != 0;
		if (readRaw)
		{
			if (_lastSectorHeader.SectorMode == 1)
			{
				// Mode1 padded to Mode2 layout
				_disc.AsSpan(rawOff + SectorSyncSize, Mode1HeaderSize).CopyTo(sb.Data);
				for (int i = Mode1HeaderSize; i < Mode2HeaderSize; i++) sb.Data[i] = 0;
				_disc.AsSpan(rawOff + SectorSyncSize + Mode1HeaderSize, DataSectorSize).CopyTo(sb.Data.AsSpan(Mode2HeaderSize));
				sb.Size = Mode2HeaderSize + DataSectorSize;
			}
			else
			{
				_disc.AsSpan(rawOff + SectorSyncSize, RawSectorOutputSize).CopyTo(sb.Data);
				sb.Size = RawSectorOutputSize;
			}
		}
		else
		{
			if (_lastSectorHeader.SectorMode != 1 && _lastSectorHeader.SectorMode != 2)
				return;
			int offset = _lastSectorHeader.SectorMode == 1 ? SectorSyncSize + Mode1HeaderSize : SectorSyncSize + Mode2HeaderSize;
			_disc.AsSpan(rawOff + offset, DataSectorSize).CopyTo(sb.Data);
			sb.Size = DataSectorSize;
		}
		sb.Position = 0;
		_currentWriteSectorBuffer = sbNum;

		// Diagnostic: log first 200 data sectors + STR FMV
		_diagDataSectors++;
		if (_diagDataSectors <= 200)
		{
			byte diagMode = _disc[rawOff + 15];
			byte diagSubmode = diagMode == 2 ? _disc[rawOff + 18] : (byte)0;
			PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn, $"Data sector #{_diagDataSectors} INT1: LBA={_currentLba} mode={diagMode:X2} submode=0x{diagSubmode:X2} rawMode={readRaw} fifoLen={sb.Size}");
		}

		// Boot signature scans
		byte[] scanData = sb.Data;
		int scanLen = sb.Size;
		if (scanLen >= 8)
		{
			if (scanData[0] == 0x01 && scanData[1] == 'C' && scanData[2] == 'D' &&
				scanData[3] == '0' && scanData[4] == '0' && scanData[5] == '1')
				PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn, $"ISO Primary Volume Descriptor at LBA={_currentLba}");
			if (scanData[0] == 'P' && scanData[1] == 'S' && scanData[2] == '-' && scanData[3] == 'X' &&
				scanData[4] == ' ' && scanData[5] == 'E' && scanData[6] == 'X' && scanData[7] == 'E')
				PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn, $"PS-X EXE header at LBA={_currentLba}");
			if (scanData[0] == 'B' && scanData[1] == 'O' && scanData[2] == 'O' && scanData[3] == 'T')
			{
				PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn, $"SYSTEM.CNF at LBA={_currentLba}");
			}
		}

		// Deliver INT1
		if (HasPendingAsyncInterrupt())
		{
			// The PREVIOUS sector's data-ready INT1 was never acknowledged by the game
			// before this sector arrived, so it's DROPPED.
			ClearAsyncInterrupt();
		}
		_asyncResponseFifo.Push(GetStat());
		SetAsyncInterrupt(IntDataReady);
	}

	private void ProcessXAADPCMSector(int rawOff)
	{
		// Filter check
		if ((_modeBits & 0x08) != 0 &&
			(_lastSectorSubheader.FileNumber != _xaFilterFileNumber ||
			 _lastSectorSubheader.ChannelNumber != _xaFilterChannelNumber))
		{
			return;
		}

		if (!_xaCurrentSet)
		{
			if (_lastSectorSubheader.ChannelNumber == 255 &&
				((_modeBits & 0x08) == 0 || _xaFilterChannelNumber != 255))
				return;
			_xaCurrentFileNumber = _lastSectorSubheader.FileNumber;
			_xaCurrentChannelNumber = _lastSectorSubheader.ChannelNumber;
			_xaCurrentSet = true;
			Array.Clear(_xaLastSamples);
			PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn,
				$"[XA] New stream: file=0x{_xaCurrentFileNumber:X2} channel=0x{_xaCurrentChannelNumber:X2} coding=0x{_lastSectorSubheader.CodinginfoBits:X2}");
		}
		else if (_lastSectorSubheader.FileNumber != _xaCurrentFileNumber ||
				 _lastSectorSubheader.ChannelNumber != _xaCurrentChannelNumber)
			return;

		if (_lastSectorSubheader.SubmodeEof)
		{
			ResetCurrentXAFile();
			PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn,
				$"[XA] Stream end (EOF): file=0x{_lastSectorSubheader.FileNumber:X2} ch=0x{_lastSectorSubheader.ChannelNumber:X2} submode=0x{_lastSectorSubheader.SubmodeBits:X2}");
		}

		_psx.Spu?.SyncPendingSamples();

		bool stereo = _lastSectorSubheader.CodingStereo;
		bool halfRate = _lastSectorSubheader.CodingHalfSampleRate;
		bool bits8 = _lastSectorSubheader.Coding8BitAdpcm;

		_xaCurrentCodinginfo = _lastSectorSubheader;

		int count = DecodeXaAdpcm(rawOff + 24, stereo, bits8);

		if (_muted || _adpcmMuted) return;

		// XA read-ahead buffer cap (queue shorts)
		// ~2 sectors gives jitter margin without measurable extra latency.
		const int XaQueueWatermark = 37632;
		if (count > 0 && _psx.Spu is { XaQueueLength: < XaQueueWatermark })
			_psx.Spu.FeedXaAdpcm(_xaSampleBuf, count, stereo, halfRate);
	}

	private void ProcessCDDASector(int rawOff, SubChannelQ subq, bool subqValid)
	{
		// CDDA Report INT1
		if (_driveState == DriveState.Playing && (_modeBits & 0x04) != 0 && subqValid)
		{
			if (_cddaReportStartDelay == 0)
			{
				byte frameNibble = (byte)(subq.AbsoluteFrameBcd >> 4);
				if (_lastCddaReportFrameNibble != frameNibble)
				{
					_lastCddaReportFrameNibble = frameNibble;
					ClearAsyncInterrupt();
					_asyncResponseFifo.Push(GetStat());
					_asyncResponseFifo.Push(subq.TrackNumberBcd);
					_asyncResponseFifo.Push(subq.IndexNumberBcd);
					if ((subq.AbsoluteFrameBcd & 0x10) != 0)
					{
						_asyncResponseFifo.Push(subq.RelativeMinuteBcd);
						_asyncResponseFifo.Push((byte)(0x80 | subq.RelativeSecondBcd));
						_asyncResponseFifo.Push(subq.RelativeFrameBcd);
					}
					else
					{
						_asyncResponseFifo.Push(subq.AbsoluteMinuteBcd);
						_asyncResponseFifo.Push(subq.AbsoluteSecondBcd);
						_asyncResponseFifo.Push(subq.AbsoluteFrameBcd);
					}
					byte channel = (byte)(subq.AbsoluteSecondBcd & 1u);
					short peak = GetPeakVolume(rawOff, channel);
					ushort peakValue = (ushort)((channel << 15) | (ushort)peak);
					_asyncResponseFifo.Push((byte)peakValue);
					_asyncResponseFifo.Push((byte)(peakValue >> 8));
					SetAsyncInterrupt(IntDataReady);
				}
			}
			else _cddaReportStartDelay--;
		}

		if (_muted || _cddaAutoPausePending) return;
		_psx.Spu?.SyncPendingSamples();

		const int CddaQueueWatermark = 4704;
		if (_psx.Spu != null && _psx.Spu.XaQueueLength < CddaQueueWatermark)
			_psx.Spu.FeedCdda(_disc, rawOff);
	}

	private short GetPeakVolume(int rawOff, byte channel)
	{
		// Compute peak across the sector for the chosen channel (0/1).
		if (_disc == null) return 0;
		int end = Math.Min(_disc.Length, rawOff + RawSectorSize);
		short peak = 0;
		for (int i = rawOff + channel * 2; i + 1 < end; i += 4)
		{
			short s = (short)(_disc[i] | (_disc[i + 1] << 8));
			if (s > peak) peak = s;
		}
		return peak;
	}

	private void ClearSectorBuffers()
	{
		_currentReadSectorBuffer = 0;
		_currentWriteSectorBuffer = 0;
		for (int i = 0; i < NumSectorBuffers; i++)
		{
			_sectorBuffers[i].Position = 0;
			_sectorBuffers[i].Size = 0;
		}
		_requestRegister &= 0x7F;  // clear BFRD
		UpdateStatusRegister();
	}

	private void CheckForSectorBufferReadComplete()
	{
		SectorBuffer sb = _sectorBuffers[_currentReadSectorBuffer];
		bool stillHasData = sb.Position < sb.Size;
		if (!stillHasData)
		{
			_requestRegister &= 0x7F;  // clear BFRD
			sb.Position = 0;
			sb.Size = 0;
			UpdateStatusRegister();
		}

		// Missed-sector redelivery
		SectorBuffer nextSb = _sectorBuffers[_currentWriteSectorBuffer];
		if (nextSb.Position == 0 && nextSb.Size > 0 && !HasPendingAsyncInterrupt() && IsReading())
		{
			_asyncResponseFifo.Push(GetStat());
			_pendingAsyncInterrupt = IntDataReady;
			int delay = Math.Min(_driveEvent.IsActive ? _driveEvent.GetTicksUntilNextExecution() : MissedInt1DelayCycles, MissedInt1DelayCycles);
			_asyncInterruptEvent.Schedule(delay);
		}
	}

	// =====================================================================
	// SubQ helpers
	// =====================================================================

	private SubChannelQ GetSectorSubQ(uint lba)
	{
		if (_sbiReplacement != null && _sbiReplacement.TryGetValue(lba, out byte[] subQ))
		{
			if (_diagSbiHits < 64)
			{
				PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.Warn,
					$"[CDROM/SBI] Protected SubQ at LBA={lba}: replacement abs={subQ[7]:X2}:{subQ[8]:X2}:{subQ[9]:X2} (CRC invalid)");
				_diagSbiHits++;
			}
			return new SubChannelQ
			{
				ControlBits = subQ[0],
				TrackNumberBcd = subQ[1],
				IndexNumberBcd = subQ[2],
				RelativeMinuteBcd = subQ[3],
				RelativeSecondBcd = subQ[4],
				RelativeFrameBcd = subQ[5],
				AbsoluteMinuteBcd = subQ[7],
				AbsoluteSecondBcd = subQ[8],
				AbsoluteFrameBcd = subQ[9],
				IsCrcValid = false,
			};
		}

		return SynthesizeSubQ(lba);
	}

	private SubChannelQ SynthesizeSubQ(uint lba)
	{
		DiscTrack track = FindTrackForLba(lba);
		uint relLba = lba >= track.StartLba ? lba - track.StartLba : 0u;
		LbaToMsf(lba, out byte absMm, out byte absSs, out byte absFf);
		byte relMm = BinToBcd((byte)(relLba / SingleSpeedSectorsPerSecond / 60));
		byte relSs = BinToBcd((byte)((relLba / SingleSpeedSectorsPerSecond) % 60));
		byte relFf = BinToBcd((byte)(relLba % SingleSpeedSectorsPerSecond));
		byte ctrl = (byte)(track.IsAudio ? 0x00 : 0x40);
		return new SubChannelQ
		{
			ControlBits = ctrl,
			TrackNumberBcd = BinToBcd(track.Number),
			IndexNumberBcd = BinToBcd(1),
			RelativeMinuteBcd = relMm,
			RelativeSecondBcd = relSs,
			RelativeFrameBcd = relFf,
			AbsoluteMinuteBcd = absMm,
			AbsoluteSecondBcd = absSs,
			AbsoluteFrameBcd = absFf,
			IsCrcValid = true,
		};
	}

	// =====================================================================
	// XA decoder
	// =====================================================================

	private void ResetAudioDecoder()
	{
		ResetCurrentXAFile();
		Array.Clear(_xaLastSamples);
		_cddaAutoPausePending = false;
		_psx.Spu?.ResetXaDecoder();
	}

	private void ResetCurrentXAFile()
	{
		_xaCurrentChannelNumber = 0;
		_xaCurrentFileNumber = 0;
		_xaCurrentSet = false;
	}

	private int DecodeXaAdpcm(int dataOffset, bool stereo, bool bits8)
	{
		const int numChunks = 18;
		const int chunkSize = 128;
		const int wordsPerBlock = 28;
		int numBlocks = bits8 ? 4 : 8;
		int samplesPerChunk = wordsPerBlock * (bits8 ? 4 : 8);

		for (int i = 0; i < numChunks; i++)
		{
			int chunkBase = dataOffset + i * chunkSize;
			int chunkSampleBase = i * samplesPerChunk;

			for (int block = 0; block < numBlocks; block++)
			{
				byte hdr = _disc[chunkBase + 4 + block];
				int shift = hdr & 0x0F;
				if (shift > 12) shift = 9;
				int filter = (hdr >> 4) & 0x0F;
				if (filter > 4) filter = 0;

				int fp = XaFilterPos[filter];
				int fn = XaFilterNeg[filter];

				int prevIdx = stereo ? (block & 1) * 2 : 0;

				int outBase, outStep;
				if (stereo)
				{
					outBase = (block / 2) * (wordsPerBlock * 2) + (block & 1);
					outStep = 2;
				}
				else
				{
					outBase = block * wordsPerBlock;
					outStep = 1;
				}

				for (int word = 0; word < wordsPerBlock; word++)
				{
					int wOff = chunkBase + 16 + word * 4;
					uint wd = (uint)(_disc[wOff]
									  | (_disc[wOff + 1] << 8)
									  | (_disc[wOff + 2] << 16)
									  | (_disc[wOff + 3] << 24));

					int rawSample;
					if (bits8)
					{
						int nibble = (int)((wd >> (block * 8)) & 0xFF);
						rawSample = (short)(ushort)(nibble << 8) >> shift;
					}
					else
					{
						int nibble = (int)((wd >> (block * 4)) & 0x0F);
						rawSample = (short)(ushort)(nibble << 12) >> shift;
					}

					int s = Math.Clamp(
						rawSample
						+ ((_xaLastSamples[prevIdx] * fp) >> 6)
						+ ((_xaLastSamples[prevIdx + 1] * fn) >> 6),
						-32768, 32767);

					_xaLastSamples[prevIdx + 1] = _xaLastSamples[prevIdx];
					_xaLastSamples[prevIdx] = s;

					_xaSampleBuf[chunkSampleBase + outBase + word * outStep] = (short)s;
				}
			}
		}

		return numChunks * samplesPerChunk;
	}
}
