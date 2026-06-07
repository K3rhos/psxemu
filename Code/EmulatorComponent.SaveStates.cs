using System.IO;
using System.Text;

namespace PSXEmu;

public sealed partial class EmulatorComponent
{
	private const string SaveStateExt = ".psxemu_sav";
	private const string SaveStateDir = "savestates";
	private const uint SaveMetaMagic = 0x314D5850u; // 'PXM1'
	
	public string CurrentGameName
	{
		get
		{
			string p = SelectedDiscPath;
			
			if (string.IsNullOrWhiteSpace(p))
				return "psx";
			
			string name = Path.GetFileNameWithoutExtension(p);
			
			return string.IsNullOrWhiteSpace(name) ? "psx" : PathUtility.SanitizeFileName(name);
		}
	}
	
	private string CurrentGameId
	{
		get
		{
			string serial = Core?.Cdrom?.GameSerial;
			
			string id = string.IsNullOrWhiteSpace(serial) ? CurrentGameName : serial.Trim();
			
			return PathUtility.SanitizeFileName(id);
		}
	}
	
	private string CurrentSaveDir => $"{SaveStateDir}/{CurrentGameId}";
	
	
	
	public List<string> ListSaveStates()
	{
		var list = new List<string>();
		
		try
		{
			string dir = CurrentSaveDir;
			
			if (!FileSystem.Data.DirectoryExists(dir))
				return list;
			
			foreach (var f in FileSystem.Data.FindFile(dir, "*" + SaveStateExt))
				list.Add(Path.GetFileName(f));
			
			list.Sort(StringComparer.OrdinalIgnoreCase);
		}
		catch
		{
			// ignored
		}

		return list;
	}
	
	
	
	public string SuggestNextSaveName()
	{
		string game = CurrentGameName;
		
		int best = 0;
		
		foreach (var f in ListSaveStates())
		{
			string baseName = Path.GetFileNameWithoutExtension(f);
			
			int hash = baseName.LastIndexOf('#');
			
			if (hash >= 0 && baseName.StartsWith(game, StringComparison.OrdinalIgnoreCase) && int.TryParse(baseName.AsSpan(hash + 1), out int n) && n > best)
				best = n;
		}
		
		return $"{game}_#{(best + 1):D3}{SaveStateExt}";
	}
	
	
	
	public bool SaveStateToSlot(string _FileName, out string _Error)
	{
		_Error = null;
		
		var core = Core;
		
		if (core == null)
		{
			_Error = "No game running.";
			
			return false;
		}

		if (string.IsNullOrWhiteSpace(_FileName))
		{
			_Error = "Empty file name.";
			
			return false;
		}
		
		_FileName = Path.GetFileName(_FileName.Trim());
		
		if (!_FileName.EndsWith(SaveStateExt, StringComparison.OrdinalIgnoreCase))
			_FileName += SaveStateExt;

		bool wasPaused = _paused;

		try
		{
			_paused = true;

			WaitForWorkerIdle();

			byte[] coreBytes;

			using (var ms = new MemoryStream())
			{
				core.SaveState(ms);
				coreBytes = ms.ToArray();
			}

			// Wrap the core state with [magic][game-id] so a load can verify the save
			// really belongs to this game even if its file was moved/renamed.
			byte[] bytes;

			using (var ms = new MemoryStream())
			{
				using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
				{
					w.Write(SaveMetaMagic);
					w.Write(CurrentGameId);
					w.Write(coreBytes);
				}

				bytes = ms.ToArray();
			}

			string dir = CurrentSaveDir;

			FileSystem.Data.CreateDirectory(dir);

			using (var s = FileSystem.Data.OpenWrite($"{dir}/{_FileName}"))
				s.Write(bytes, 0, bytes.Length);

			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Info, $"[SaveState] saved {dir}/{_FileName} ({bytes.Length} bytes)");

			return true;
		}
		catch (Exception _Exception)
		{
			_Error = _Exception.Message;

			return false;
		}
		finally
		{
			_paused = wasPaused;
		}
	}
	
	
	
	public bool LoadStateFromSlot(string _FileName, out string _Error)
	{
		_Error = null;
		
		var core = Core;

		if (core == null)
		{
			_Error = "No game running.";
			
			return false;
		}
		
		_FileName = Path.GetFileName(_FileName);
		
		string path = $"{CurrentSaveDir}/{_FileName}";

		bool wasPaused = _paused;

		try
		{
			if (!FileSystem.Data.FileExists(path))
			{
				_Error = "Save not found: " + _FileName;

				return false;
			}

			byte[] bytes = FileSystem.Data.ReadAllBytes(path).ToArray();

			// Strip + validate the [magic][game-id] header. The per-game folder
			// already scopes loads, but this guards against a save file physically
			// moved into the wrong folder, never load another game's state.
			byte[] coreBytes = bytes;

			if (bytes.Length >= 4 && BitConverter.ToUInt32(bytes, 0) == SaveMetaMagic)
			{
				string savedId;
				int headerLen;

				using (var hs = new MemoryStream(bytes))
				using (var r = new BinaryReader(hs, Encoding.UTF8))
				{
					r.ReadUInt32();
					savedId = r.ReadString();
					headerLen = (int)hs.Position;
				}

				if (!string.Equals(savedId, CurrentGameId, StringComparison.OrdinalIgnoreCase))
				{
					_Error = $"That save belongs to a different game ({savedId}), not {CurrentGameId}.";
					
					return false;
				}

				coreBytes = new byte[bytes.Length - headerLen];

				Array.Copy(bytes, headerLen, coreBytes, 0, coreBytes.Length);
			}

			_paused = true;

			WaitForWorkerIdle();

			using (var ms = new MemoryStream(coreBytes))
				core.LoadState(ms);

			// Refresh the display snapshot so the loaded frame is visible immediately
			// (we're paused, so the worker won't do it until resume).
			core.Gpu.SnapshotVram();

			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Info, $"[SaveState] loaded {path} ({bytes.Length} bytes)");

			return true;
		}
		catch (Exception _Exception)
		{
			_Error = _Exception.Message;

			return false;
		}
		finally
		{
			_paused = wasPaused;
		}
	}
	
	
	
	public bool DeleteSaveState(string _FileName, out string _Error)
	{
		_Error = null;

		try
		{
			string path = $"{CurrentSaveDir}/{Path.GetFileName(_FileName)}";
			
			if (FileSystem.Data.FileExists(path))
				FileSystem.Data.DeleteFile(path);
			
			return true;
		}
		catch (Exception _Exception)
		{
			_Error = _Exception.Message;
			
			return false;
		}
	}
}
