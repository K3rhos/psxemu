using Sandbox;

namespace PSXEmu;

/// <summary>
/// PSX Memory Card slot 1 emulation.
///
/// Communicates over the SIO0 bus, sharing it with the controller.
/// The CPU addresses the memory card by sending 0x81 as the first byte of
/// a transfer (0x01 selects the controller). Once selected, the card
/// services three commands:
///
///   0x52  Read  : returns 128 bytes from the addressed sector
///   0x57  Write : stores 128 bytes into the addressed sector
///   0x53  GetID : returns the card type identifier bytes
///
/// Card image: 128 KB (1 024 sectors * 128 bytes), raw binary.
/// Persisted to FileSystem.Data at "memcards/mcd1.mcd".
/// An all-0xFF image (blank flash) is used when no save file exists;
/// the PSX BIOS automatically formats blank cards on first access.
///
/// </summary>
public class PsxMemoryCard
{
	// Constants

	public const int MemCardSize = 128 * 1024;       // 128 KB total
	public const int FrameSize = 128;               // bytes per sector/frame
	public const int FrameCount = MemCardSize / FrameSize; // 1 024 frames

	private const string SaveFolder = "memcards";
	private const string SavePath = "memcards/mcd1.mcd";

	// SIO state machine

	private enum State : byte
	{
		Idle,
		Command,

		// Read command (0x52)
		ReadCardID1,
		ReadCardID2,
		ReadAddressMSB,
		ReadAddressLSB,
		ReadACK1,
		ReadACK2,
		ReadConfirmAddressMSB,
		ReadConfirmAddressLSB,
		ReadData,
		ReadChecksum,
		ReadEnd,

		// Write command (0x57)
		WriteCardID1,
		WriteCardID2,
		WriteAddressMSB,
		WriteAddressLSB,
		WriteData,
		WriteChecksum,
		WriteACK1,
		WriteACK2,
		WriteEnd,

		// Get ID command (0x53)
		GetIDCardID1,
		GetIDCardID2,
		GetIDACK1,
		GetIDACK2,
		GetID1,
		GetID2,
		GetID3,
		GetID4,
	}

	// FLAG byte bits (returned after command byte):
	//   bit 3 = no_write_yet (set until first write, cleared afterwards)
	//   bit 2 = write_error  (not used here)
	private byte _flag = 0x08; // starts with no_write_yet set

	private State _state = State.Idle;
	private ushort _address;        // current sector address (0-1023)
	private int _sectorOffset;   // byte index within the current 128-byte sector
	private byte _checksum;       // running XOR accumulator
	private byte _lastByte;       // last byte received from CPU (used for echo)
	private bool _changed;        // true when data has been written since last save

	private readonly byte[] _data = new byte[MemCardSize];

	// Construction / lifecycle

	public PsxMemoryCard()
	{
		LoadOrFormat();
	}

	/// <summary>
	/// Hard reset (emulator reset button).
	/// Saves any pending changes and resets the SIO transfer state.
	/// The card data itself is preserved (it is non-volatile flash).
	/// </summary>
	public void Reset()
	{
		SaveIfDirty();
		ResetTransferState();
		_flag = 0x08; // no_write_yet set again
	}

	/// <summary>Reset only the SIO transfer state (called when /CS is deasserted).</summary>
	public void ResetTransferState()
	{
		_state = State.Idle;
		_address = 0;
		_sectorOffset = 0;
		_checksum = 0;
		_lastByte = 0;
	}

	// ---- Save-state ---- (card data + SIO transfer state; no scheduler event.)
	public void SaveState(StateWriter w)
	{
		w.Bytes(_data);
		w.U8(_flag); w.S32((int)_state); w.U16(_address);
		w.S32(_sectorOffset); w.U8(_checksum); w.U8(_lastByte); w.Bool(_changed);
	}

	public void LoadState(StateReader r)
	{
		r.Bytes(_data);
		_flag = r.U8(); _state = (State)r.S32(); _address = r.U16();
		_sectorOffset = r.S32(); _checksum = r.U8(); _lastByte = r.U8(); _changed = r.Bool();
	}

	// Force-flush any unsaved data to disk (called on emulator shutdown).
	public void Flush() => SaveIfDirty();

	// SIO transfer

	/// <summary>
	/// Exchange one byte with the CPU over SIO0.
	/// Returns (rx byte to send back, ack flag).
	/// Ack=false ends the active-device session in PsxController.
	/// </summary>
	public (byte rx, bool ack) Transfer(byte tx)
	{
		byte rx = 0xFF;
		bool ack = false;

		switch (_state)
		{
			// Idle: wait for card-select byte
			case State.Idle:
				if (tx == 0x81)
				{
					rx = 0xFF;
					ack = true;
					_state = State.Command;
				}
				break;

			// Command dispatch
			case State.Command:
				rx = _flag;
				switch (tx)
				{
					case 0x52: ack = true; _state = State.ReadCardID1; break; // Read
					case 0x57: ack = true; _state = State.WriteCardID1; break; // Write
					case 0x53: ack = true; _state = State.GetIDCardID1; break; // Get ID
					default:
						ack = false;
						_state = State.Idle;
						break;
				}
				break;

			// Read sequence
			// CPU:  81 52 00 MSB LSB  00*128  00  00
			// Card: FF fl 5A 5D 00 LB 5C 5D aM aL data[128] chk 47
			// (fl=FLAG, LB=last byte echo, aM/aL=confirmed address)
			case State.ReadCardID1: rx = 0x5A; ack = true; _state = State.ReadCardID2; break;
			case State.ReadCardID2: rx = 0x5D; ack = true; _state = State.ReadAddressMSB; break;

			case State.ReadAddressMSB:
				rx = 0x00;
				ack = true;
				_address = (ushort)((_address & 0x00FF) | (tx << 8));
				_address &= 0x3FF;
				_state = State.ReadAddressLSB;
				break;

			case State.ReadAddressLSB:
				rx = _lastByte;   // echo back the MSB the CPU just sent
				ack = true;
				_address = (ushort)((_address & 0xFF00) | tx);
				_address &= 0x3FF;
				_sectorOffset = 0;
				_state = State.ReadACK1;
				break;

			case State.ReadACK1: rx = 0x5C; ack = true; _state = State.ReadACK2; break;
			case State.ReadACK2: rx = 0x5D; ack = true; _state = State.ReadConfirmAddressMSB; break;
			case State.ReadConfirmAddressMSB: rx = (byte)(_address >> 8); ack = true; _state = State.ReadConfirmAddressLSB; break;
			case State.ReadConfirmAddressLSB: rx = (byte)(_address); ack = true; _state = State.ReadData; break;

			case State.ReadData:
				{
					int offset = _address * FrameSize + _sectorOffset;
					byte bits = offset < _data.Length ? _data[offset] : (byte)0xFF;

					// Checksum = MSB XOR LSB XOR data[0] XOR ... XOR data[127]
					if (_sectorOffset == 0)
						_checksum = (byte)((_address >> 8) ^ (_address & 0xFF) ^ bits);
					else
						_checksum ^= bits;

					rx = bits;
					ack = true;

					if (++_sectorOffset == FrameSize)
					{
						_sectorOffset = 0;
						_state = State.ReadChecksum;
					}
					break;
				}

			case State.ReadChecksum: rx = _checksum; ack = true; _state = State.ReadEnd; break;
			case State.ReadEnd: rx = 0x47; ack = true; _state = State.Idle; break; // ACK=true: interrupt-driven games wait for this final ACK before deasserting /CS

			// Write sequence
			// CPU:  81 57 00 MSB LSB  data[128]   chk  00  00
			// Card: FF fl 5A 5D 00 LB 00*128      chk  5C  5D  47
			case State.WriteCardID1: rx = 0x5A; ack = true; _state = State.WriteCardID2; break;
			case State.WriteCardID2: rx = 0x5D; ack = true; _state = State.WriteAddressMSB; break;

			case State.WriteAddressMSB:
				rx = 0x00;
				ack = true;
				_address = (ushort)((_address & 0x00FF) | (tx << 8));
				_address &= 0x3FF;
				_state = State.WriteAddressLSB;
				break;

			case State.WriteAddressLSB:
				rx = _lastByte;   // echo back the MSB
				ack = true;
				_address = (ushort)((_address & 0xFF00) | tx);
				_address &= 0x3FF;
				_sectorOffset = 0;
				_state = State.WriteData;
				break;

			case State.WriteData:
				{
					if (_sectorOffset == 0)
					{
						_checksum = (byte)((_address >> 8) ^ (_address & 0xFF) ^ tx);
						_flag = 0x00; // clear no_write_yet on first write
					}
					else
					{
						_checksum ^= tx;
					}

					int offset = _address * FrameSize + _sectorOffset;
					if (offset < _data.Length)
					{
						_changed |= _data[offset] != tx;
						_data[offset] = tx;
					}

					rx = _lastByte;
					ack = true;

					if (++_sectorOffset == FrameSize)
					{
						_sectorOffset = 0;
						_state = State.WriteChecksum;
						// Persist after every completed sector so saves survive crashes
						if (_changed) SaveIfDirty();
					}
					break;
				}

			case State.WriteChecksum: rx = _checksum; ack = true; _state = State.WriteACK1; break;
			case State.WriteACK1: rx = 0x5C; ack = true; _state = State.WriteACK2; break;
			case State.WriteACK2: rx = 0x5D; ack = true; _state = State.WriteEnd; break;
			case State.WriteEnd: rx = 0x47; ack = false; _state = State.Idle; break;

			// Get ID sequence
			case State.GetIDCardID1: rx = 0x5A; ack = true; _state = State.GetIDCardID2; break;
			case State.GetIDCardID2: rx = 0x5D; ack = true; _state = State.GetIDACK1; break;
			case State.GetIDACK1: rx = 0x5C; ack = true; _state = State.GetIDACK2; break;
			case State.GetIDACK2: rx = 0x5D; ack = true; _state = State.GetID1; break;
			case State.GetID1: rx = 0x04; ack = true; _state = State.GetID2; break;
			case State.GetID2: rx = 0x00; ack = true; _state = State.GetID3; break;
			case State.GetID3: rx = 0x00; ack = true; _state = State.GetID4; break;
			case State.GetID4: rx = 0x80; ack = true; _state = State.Command; break; // loops back to await next cmd
		}

		_lastByte = tx;
		return (rx, ack);
	}

	// Persistence

	private void LoadOrFormat()
	{
		try
		{
			if (FileSystem.Data.FileExists(SavePath))
			{
				var raw = FileSystem.Data.ReadAllBytes(SavePath);
				if (!raw.IsEmpty)
				{
					var bytes = raw.ToArray();
					if (bytes.Length == MemCardSize)
					{
						bytes.CopyTo(_data, 0);
						PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Info,
							$"Memory card loaded ({MemCardSize / 1024} KB) from {SavePath}");
						return;
					}
				}
			}
		}
		catch (System.Exception ex)
		{
			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Warn,
				$"Memory card load failed ({ex.Message}), generating fresh formatted card");
		}

		// No valid save file, generate a properly formatted empty card.
		// PSX BIOS sees a valid "MC" header and reports "no save data" rather than
		// showing the "card not formatted" prompt on every game access.
		FormatBlank();
		_changed = true; // persist immediately so the file exists for next launch
		PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Info,
			"Memory card: generated fresh formatted card");
	}

	/// <summary>
	/// Writes a valid empty PSX memory card image into <see cref="_data"/>.
	///
	/// Block 0, frame layout (each frame = 128 bytes):
	///   Frame  0      : Header:  'M','C', 0*125, checksum
	///   Frames 1-15   : Directory entries (free): 0xA0, 0*7, 0xFF, 0xFF, 0*118, checksum
	///   Frames 16-35  : Broken-sector list:       0xFF*4, 0*4, 0xFF*2, 0*118, checksum
	///   Frames 36-55  : Broken-sector replacement data: all 0x00
	///   Frames 56-62  : Unused: all 0x00
	///   Frame  63     : Write-test frame: copy of frame 0
	/// Frames 64-1023  : Data area: all 0xFF
	/// </summary>
	private void FormatBlank()
	{
		// 1. Fill everything with 0xFF (erased flash)
		System.Array.Fill(_data, (byte)0xFF);

		// 2. Header frame (frame 0, offset 0)
		{
			int off = 0;
			System.Array.Fill(_data, (byte)0x00, off, FrameSize);
			_data[off + 0] = (byte)'M';
			_data[off + 1] = (byte)'C';
			_data[off + 0x7F] = FrameChecksum(off);
		}

		// 3. Directory frames (frames 1-15, offsets 128-1919)
		for (int frame = 1; frame < 16; frame++)
		{
			int off = frame * FrameSize;
			System.Array.Fill(_data, (byte)0x00, off, FrameSize);
			_data[off + 0] = 0xA0; // free slot
			_data[off + 8] = 0xFF; // next-block pointer (none)
			_data[off + 9] = 0xFF;
			_data[off + 0x7F] = FrameChecksum(off);
		}

		// 4. Broken-sector list (frames 16-35)
		for (int frame = 16; frame < 36; frame++)
		{
			int off = frame * FrameSize;
			System.Array.Fill(_data, (byte)0x00, off, FrameSize);
			_data[off + 0] = 0xFF;
			_data[off + 1] = 0xFF;
			_data[off + 2] = 0xFF;
			_data[off + 3] = 0xFF;
			_data[off + 8] = 0xFF;
			_data[off + 9] = 0xFF;
			_data[off + 0x7F] = FrameChecksum(off);
		}

		// 5. Broken-sector replacement data (frames 36-55), all 0x00
		for (int frame = 36; frame < 56; frame++)
			System.Array.Fill(_data, (byte)0x00, frame * FrameSize, FrameSize);

		// 6. Unused frames (frames 56-62), all 0x00
		for (int frame = 56; frame < 63; frame++)
			System.Array.Fill(_data, (byte)0x00, frame * FrameSize, FrameSize);

		// 7. Write-test frame (frame 63), copy of header frame
		System.Array.Copy(_data, 0, _data, 63 * FrameSize, FrameSize);
	}

	/// <summary>
	/// Returns the checksum byte for one 128-byte frame: XOR of bytes [offset .. offset+126].
	/// Stored at byte offset+127 by convention; this helper just computes the value.
	/// </summary>
	private byte FrameChecksum(int offset)
	{
		byte xor_ = _data[offset];
		for (int i = 1; i < FrameSize - 1; i++)
			xor_ ^= _data[offset + i];
		return xor_;
	}

	private void SaveIfDirty()
	{
		if (!_changed) return;
		_changed = false;

		try
		{
			if (!FileSystem.Data.DirectoryExists(SaveFolder))
				FileSystem.Data.CreateDirectory(SaveFolder);

			FileSystem.Data.WriteAllBytes(SavePath, _data);
			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Info,
				$"Memory card saved to {SavePath}");
		}
		catch (System.Exception ex)
		{
			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Error,
				$"Memory card save failed: {ex.Message}");
		}
	}
}
