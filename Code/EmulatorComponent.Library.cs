using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Sandbox.Rendering;

namespace PSXEmu;

public sealed partial class EmulatorComponent
{
	public sealed class LaunchEntry
	{
		public string Path { get; init; }
		public string DisplayName { get; init; }
		public string Subtitle { get; init; }
		public string Source { get; init; }
		public string SerialId { get; init; }
		public Texture CoverTexture { get; init; }
		public bool IsBios { get; init; }
	}
	
	
	
	public void RefreshLaunchLibrary()
	{
		ApplyLaunchLibrarySnapshot(BuildLaunchLibrarySnapshot(_RefreshBios: true, _RefreshGames: true));
		
		_ = RefreshGameCoversAsync();
	}

	public Task RefreshBiosLibraryAsync() => RefreshLaunchLibraryAsync(_RefreshBios: true, _RefreshGames: false);

	public Task RefreshGameLibraryAsync() => RefreshLaunchLibraryAsync(_RefreshBios: false, _RefreshGames: true);
	
	
	
	public async Task RefreshLaunchLibraryAsync(bool _RefreshBios, bool _RefreshGames)
	{
		if (IsRefreshing)
			return;

		IsRefreshing = true;
		ErrorMessage = null;

		if (_RefreshBios)
			_availableBios.Clear();

		if (_RefreshGames)
			_availableGames.Clear();

		try
		{
			var snapshot = await Task.RunInThreadAsync(() => BuildLaunchLibrarySnapshot(_RefreshBios, _RefreshGames));
			
			ApplyLaunchLibrarySnapshot(snapshot);
			
			if (_RefreshGames)
				await RefreshGameCoversAsync();
		}
		finally
		{
			IsRefreshing = false;
		}
	}
	
	
	
	public bool LaunchSelection(string _BiosPath, string _DiscPath = null)
	{
		if (string.IsNullOrWhiteSpace(_BiosPath))
		{
			ErrorMessage = "Select a BIOS before launching.";
			
			return false;
		}
		
		ErrorMessage = null;
		IsLaunching = true;

		try
		{
			ShutdownEmulator();

			if (!PathUtility.TryReadMountedOrDataBytes(_BiosPath, out var biosData))
			{
				ErrorMessage = $"BIOS not found: {_BiosPath}";
				
				return false;
			}

			if (biosData.Length < 512 * 1024)
			{
				ErrorMessage = $"BIOS file too small ({biosData.Length} bytes, expected 512 KB)";
				return false;
			}

			Core = new Psx();
			Core.LoadBios(biosData);

			if (!string.IsNullOrWhiteSpace(_DiscPath) && _DiscPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				// PS-X EXE side-load it instead of mounting a disc.
				// The CPU injects it when the BIOS reaches the shell entry (0x80030000).
				if (!PathUtility.TryReadMountedOrDataBytes(_DiscPath, out var exeData))
				{
					ErrorMessage = $"EXE not found: {_DiscPath}";
					
					return false;
				}
				
				if (exeData.Length < 0x800 || !IsPsxExeHeader(exeData))
				{
					ErrorMessage = $"Not a PS-X EXE (bad header): {_DiscPath}";
					
					return false;
				}
				
				Core.LoadExe(exeData);
				
				PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Info, $"PS-EXE selected for side-load: {_DiscPath} ({exeData.Length} bytes)");
			}
			else if (!string.IsNullOrWhiteSpace(_DiscPath))
			{
				byte[] discData;
				PsxCdrom.DiscTrack[] discTracks = null;

				if (_DiscPath.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
				{
					// Parse the CUE sheet to learn the track layout.
					if (!TryParseCue(_DiscPath, out _, out var cueTracks, out string[] allTrackPaths))
					{
						ErrorMessage = $"Failed to parse CUE sheet: {_DiscPath}";
						
						return false;
					}

					if (allTrackPaths.Length == 1)
					{
						// Single-file CUE: all tracks packed into one .bin.
						if (!PathUtility.TryReadMountedOrDataBytes(allTrackPaths[0], out discData))
						{
							ErrorMessage = $"Disc image not found: {allTrackPaths[0]}";
							
							return false;
						}

						discTracks = new PsxCdrom.DiscTrack[cueTracks.Length];
						
						for (int i = 0; i < cueTracks.Length; i++)
							discTracks[i] = new PsxCdrom.DiscTrack(cueTracks[i].Number, cueTracks[i].IsAudio, cueTracks[i].Index01Lba, cueTracks[i].Index01Lba);
					}
					else
					{
						// Multi-file CUE: one .bin per track.
						var trackBuffers = new byte[allTrackPaths.Length][];
						
						for (int i = 0; i < allTrackPaths.Length; i++)
						{
							if (!PathUtility.TryReadMountedOrDataBytes(allTrackPaths[i], out trackBuffers[i]))
							{
								ErrorMessage = $"Track file not found: {allTrackPaths[i]}";
								
								return false;
							}
						}

						// Compute each track's INDEX 01 start in the concatenated virtual disc.
						var fileStartSectors = new uint[allTrackPaths.Length];
						
						uint sectorCursor = 0;
						
						for (int i = 0; i < allTrackPaths.Length; i++)
						{
							fileStartSectors[i] = sectorCursor;
							
							sectorCursor += (uint)(trackBuffers[i].Length / PsxCdrom.RawSectorSize);
						}

						var updatedTracks = new PsxCdrom.DiscTrack[cueTracks.Length];
						
						for (int i = 0; i < cueTracks.Length; i++)
						{
							int fileIndex = Math.Clamp(cueTracks[i].FileIndex, 0, fileStartSectors.Length - 1);
							uint startSector = fileStartSectors[fileIndex] + cueTracks[i].Index01Lba;
							
							updatedTracks[i] = new PsxCdrom.DiscTrack(cueTracks[i].Number, cueTracks[i].IsAudio, startSector, startSector);
						}
						
						discTracks = updatedTracks;

						// Concatenate all track buffers into a single contiguous disc image.
						int totalLen = 0;
						
						foreach (var buf in trackBuffers)
							totalLen += buf.Length;
						
						discData = new byte[totalLen];
						
						int offset = 0;
						
						foreach (var buf in trackBuffers)
						{
							Buffer.BlockCopy(buf, 0, discData, offset, buf.Length);
							
							offset += buf.Length;
						}
					}

					PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Info, $"CUE loaded: {discTracks.Length} tracks, {discData.Length / PsxCdrom.RawSectorSize} total sectors");
				}
				else
				{
					// Plain .bin, single data track at LBA 0.
					if (!PathUtility.TryReadMountedOrDataBytes(_DiscPath, out discData))
					{
						ErrorMessage = $"Disc image not found: {_DiscPath}";
						
						return false;
					}
				}

				Core.LoadDisc(discData, discTracks);

				// LibCrypt SBI lookup.
				// Some PAL games requires an .sbi file to work
				TryLoadSbiForDisc(_DiscPath);

				string serial = Core.Cdrom.GameSerial;
				
				if (PsxSbi.RequiresSbi(serial) && !Core.Cdrom.HasSbi)
				{
					ErrorMessage = $"This game ({serial}) is LibCrypt-protected and requires an SBI file to run correctly. Place '{serial}.sbi' (or any matching .sbi) next to the disc image and reload.";
					
					PsxLog.Write(PsxLogCategory.CDROM, PsxLogLevel.GameError, ErrorMessage);
					
					return false;
				}
				
				if (Core.Cdrom.HasSbi)
				{
					PsxLog.Write(PsxLogCategory.SBI, PsxLogLevel.Info, $"Loaded for {serial}: {Core.Cdrom.SbiReplacementCount} protected sectors");
				}
			}

			Core.Reset();

			// If auto trace is enabled, immediately start after Reset (= emulated cycle 0)
			if (AutoTrace)
			{
				string serial = !string.IsNullOrEmpty(Core.Cdrom.GameSerial) ? Core.Cdrom.GameSerial.Replace('_', '-').Replace('.', '-') : "no-disc";
				
				string tracePath = $"traces/auto/{serial}.txt";
				
				Core.Trace.Enable(tracePath, AutoTraceFrameInterval);
				
				PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Info, $"[TRACE] Auto-trace started -> data/{tracePath} (interval={AutoTraceFrameInterval}f, max={AutoTraceFrames}f)");
			}
			
			Core.Gpu.InitGpu(DisplayFilter, ScanlineStrength, ScanlineSharpness, ScanlineFrequency, PhosphorMaskStrength, CrtColorBoost, GPURasterizer, GpuRasterScale);
			
			ScreenTexture = Core.Gpu.OutputTexture;

			_camera = Scene.Camera;
			
			if (_camera.IsValid() && Core.Gpu.RenderCommandList != null)
				_camera.AddCommandList(Core.Gpu.RenderCommandList, Stage.AfterOpaque);
			
			InitAudioStream();
			
			const int audSize = PsxConstants.MaxSpuSamplesPerFrame * PsxConstants.SpuChannels;
			
			_audBufs = new short[4][];
			
			for (int i = 0; i < 4; i++)
				_audBufs[i] = new short[audSize];
			
			_audioRingBuffer = new short[PsxConstants.SpuSampleRate * PsxConstants.SpuChannels * AudioRingSeconds];
			_audioDrainBuffer = new short[audSize * 4];
			_audioRingReadPos = 0;
			_audioRingWritePos = 0;
			_audioRingCount = 0;

			_frameChannel = Channel.CreateBounded<FramePacket>(2);
			_frameSemaphore = new SemaphoreSlim(0, 4);
			_cts = new CancellationTokenSource();
			_lastWorkerFrameTick = 0;
			_lastPresentedFrameTick = 0;
			_workerFaultMessage = null;
			_frameDebt = 0;
			_paused = false;
			_inputCooldown = 2;

			_biosPath = _BiosPath;
			_discPath = _DiscPath;
			
			SelectedBiosPath = _BiosPath;
			SelectedDiscPath = _DiscPath;
			
			IsReady = true;

			GameTask.RunInThreadAsync(EmulationLoop);
			
			return true;
		}
		catch (Exception _Exception)
		{
			ErrorMessage = $"Failed to start: {_Exception.Message}";
			
			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Fatal, ErrorMessage);
			
			ShutdownEmulator();
			
			return false;
		}
		finally
		{
			IsLaunching = false;
		}
	}
	
	
	
	private sealed class LaunchLibrarySnapshot
	{
		public bool RefreshBios { get; init; }
		public bool RefreshGames { get; init; }
		public List<LaunchEntry> Bios { get; init; } = [];
		public List<LaunchEntry> Games { get; init; } = [];
		public string Error { get; init; }
	}
	
	
	
	private LaunchLibrarySnapshot BuildLaunchLibrarySnapshot(bool _RefreshBios, bool _RefreshGames)
	{
		var bios = _RefreshBios ? [] : _availableBios.Select(CloneEntry).ToList();
		var games = _RefreshGames ? [] : _availableGames.Select(CloneEntry).ToList();

		try
		{
			if (_RefreshBios)
			{
				foreach (var entry in DiscoverFiles(_IsBios: true))
					bios.Add(entry);
			}

			if (_RefreshGames)
			{
				foreach (var entry in DiscoverFiles(_IsBios: false))
				{
					bool matchesDetectedBios = bios.Any(detectedBios =>
						PathUtility.Equals(detectedBios.Path, entry.Path) ||
						string.Equals(System.IO.Path.GetFileName(detectedBios.Path), System.IO.Path.GetFileName(entry.Path), StringComparison.OrdinalIgnoreCase));

					if (matchesDetectedBios || LooksLikeBios(entry.Path))
						continue;

					games.Add(entry);
				}
			}

			return new LaunchLibrarySnapshot
			{
				RefreshBios = _RefreshBios,
				RefreshGames = _RefreshGames,
				Bios = bios,
				Games = games
			};
		}
		catch (Exception _Exception)
		{
			string error = $"Library scan failed: {_Exception.Message}";
			
			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Error, error);
			
			return new LaunchLibrarySnapshot
			{
				RefreshBios = _RefreshBios,
				RefreshGames = _RefreshGames,
				Error = error
			};
		}
	}
	
	
	
	private void ApplyLaunchLibrarySnapshot(LaunchLibrarySnapshot _Snapshot)
	{
		if (_Snapshot.RefreshBios)
		{
			_availableBios.Clear();
			
			foreach (var entry in _Snapshot.Bios)
				_availableBios.Add(entry);
		}
		
		if (_Snapshot.RefreshGames)
		{
			_availableGames.Clear();
			
			foreach (var entry in _Snapshot.Games)
				_availableGames.Add(entry);
		}
		
		ErrorMessage = _Snapshot.Error;

		SelectedBiosPath = _availableBios.FirstOrDefault(x => PathUtility.Equals(x.Path, _biosPath))?.Path ?? _availableBios.FirstOrDefault()?.Path;
		SelectedDiscPath = _availableGames.FirstOrDefault(x => PathUtility.Equals(x.Path, _discPath))?.Path;
	}
	
	
	
	private static LaunchEntry CloneEntry(LaunchEntry _Entry)
	{
		return new LaunchEntry
		{
			Path = _Entry.Path,
			DisplayName = _Entry.DisplayName,
			Subtitle = _Entry.Subtitle,
			Source = _Entry.Source,
			SerialId = _Entry.SerialId,
			CoverTexture = _Entry.CoverTexture,
			IsBios = _Entry.IsBios
		};
	}
}
