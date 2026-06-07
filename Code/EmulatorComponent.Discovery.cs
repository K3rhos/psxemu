using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace PSXEmu;

public sealed partial class EmulatorComponent
{
	private readonly struct CueTrackEntry(byte _Number, bool _IsAudio, uint _Index01Lba, int _FileIndex)
	{
		public readonly byte Number = _Number;
		public readonly bool IsAudio = _IsAudio;
		public readonly uint Index01Lba = _Index01Lba;
		public readonly int FileIndex = _FileIndex;
	}
	
	
	
	private static readonly HashSet<string> KnownPsxBiosMd5 = new(StringComparer.OrdinalIgnoreCase)
	{
		"239665b1a3dade1b5a52c06338011044", // SCPH-1000, DTL-H1000
		"924e392ed05558ffdb115408c263dccf", // SCPH-1001 / 5003 / DTL-H1201 / H3001
		"54847e693405ffeb0359c6287434cbef", // SCPH-1002 / DTL-H1002
		"417b34706319da7cf001e76e40136c23", // SCPH-1002 / DTL-H1102
		"e2110b8a2b97a8e0b857a45d32f7e187", // SCPH-1002 / DTL-H1202 / H3002
		"849515939161e62f6b866f6853006780", // SCPH-3000 / DTL-H1000H
		"dc2b9bf8da62ec93e868cfd29f0d067d", // SCPH-1001 / DTL-H1001
		"cba733ceeff5aef5c32254f1d617fa62", // SCPH-3500
		"da27e8b6dab242d8f91a9b25d80c63b8", // SCPH-1001 / DTL-H1101
		"57a06303dfa9cf9351222dfcbb4a29d9", // SCPH-5000 / DTL-H1200 / H3000
		"8dd7d5296a650fac7319bce665a6a53c", // SCPH-5000 v3.0
		"490f666e1afb15b7362b406ed1cea246", // SCPH-5501 / 5503 / 7003
		"32736f17079d0b2b7024407c39bd3050", // SCPH-5502 / 5552
		"8e4c14f567745eff2f0408c8129f72a6", // SCPH-7000 / 7500 / 9000
		"b84be139db3ee6cbd075630aa20a6553", // SCPH-7000W
		"1e68c231d0896b7eadcad1d7d8e76129", // SCPH-7001 / 7501 / 7503 / 9001 / 9003 / 9903
		"b9d9a0286c33dc6b7237bb13cd46fdee", // SCPH-7002 / 7502 / 9002
		"8abc1b549a4a80954addc48ef02c4521", // SCPH-100
		"9a09ab7e49b422c007e6d54d7c49b965", // SCPH-101 v4.4
		"6e3735ff4c7dc899ee98981385f6f3d0", // SCPH-101 v4.5
		"b10f5e0e3d9eb60e5159690680b1e774", // SCPH-102 v4.4
		"de93caec13d1a141a40a79f5c86168d6", // SCPH-102 v4.5
		"476d68a94ccec3b9c8303bbd1daf2810", // SCPH-1000R
	};
	
	
	
	private IEnumerable<LaunchEntry> DiscoverFiles(bool _IsBios)
	{
		var entries = new Dictionary<string, LaunchEntry>(StringComparer.OrdinalIgnoreCase);
		
		var cueEntries = new Dictionary<string, (LaunchEntry Entry, string[] TrackPaths)>(StringComparer.OrdinalIgnoreCase);

		string[] preferredFolders = _IsBios ? [ "bios", "firmware" ] : [ "roms", "games", "discs", "cds", "tests" ];
		
		foreach (var fsCandidate in PathUtility.GetSearchFileSystems())
		{
			foreach (var path in PathUtility.EnumerateFilePaths(fsCandidate.FileSystem))
			{
				if (string.IsNullOrWhiteSpace(path))
					continue;

				string normalized = PathUtility.NormalizePath(path);
				string extension = Path.GetExtension(normalized);
				
				bool isBin = extension.Equals(".bin", StringComparison.OrdinalIgnoreCase);
				bool isCue = extension.Equals(".cue", StringComparison.OrdinalIgnoreCase) && !_IsBios;
				bool isExe = ScanForPSXEXE && extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !_IsBios;

				if (!isBin && !isCue && !isExe)
					continue;

				if (entries.ContainsKey(normalized) || cueEntries.ContainsKey(normalized))
					continue;

				if (isExe)
				{
					// PS-X EXE (side-loadable program, the ps1-tests suite). Only
					// accept files with the real magic header so host .exe tools
					// (e.g. gte-fuzz) are skipped.
					if (!PathUtility.TryReadMountedOrDataPrefix(normalized, 8, out var exeHdr) || !IsPsxExeHeader(exeHdr))
						continue;
					
					entries.Add(normalized, new LaunchEntry
					{
						Path = normalized,
						DisplayName = PathUtility.GetDisplayName(normalized, false),
						Subtitle = $"PS-EXE - {normalized}",
						Source = fsCandidate.Name,
						SerialId = null,
						IsBios = false,
					});
					
					continue;
				}

				if (isCue)
				{
					// Parse the CUE to find its track files, then register it as a single game entry.
					if (TryParseCue(normalized, out string dataTrackPath, out _, out string[] trackPaths))
					{
						string serialId = TryReadDiscSerial(dataTrackPath);
						
						cueEntries[normalized] =
						(
							new LaunchEntry
							{
								Path = normalized,
								DisplayName = PathUtility.GetDisplayName(normalized, false),
								Subtitle = BuildGameSubtitle(normalized, serialId),
								Source = fsCandidate.Name,
								SerialId = serialId,
								IsBios = false,
							},
							trackPaths.Select(PathUtility.NormalizePath).ToArray()
						);
					}
					
					continue;
				}

				// .bin file
				bool isValidatedBios = IsValidBiosFile(normalized);
				
				if (_IsBios && !isValidatedBios)
					continue;
				
				if (!_IsBios && isValidatedBios)
					continue;

				string serial = _IsBios ? null : TryReadDiscSerial(normalized);
				
				entries.Add(normalized, new LaunchEntry
				{
					Path = normalized,
					DisplayName = PathUtility.GetDisplayName(normalized, _IsBios),
					Subtitle = _IsBios ? normalized : BuildGameSubtitle(normalized, serial),
					Source = fsCandidate.Name,
					SerialId = serial,
					IsBios = _IsBios
				});
			}
		}
		
		if (!_IsBios)
		{
			foreach (var (cuePath, (entry, trackPaths)) in cueEntries)
			{
				entries[cuePath] = entry;
				
				foreach (var tp in trackPaths)
					entries.Remove(tp);
			}
		}

		return entries.Values
			.OrderByDescending(x => ScoreEntry(x, preferredFolders))
			.ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
			.ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase);
	}
	
	
	
	private static bool TryParseCue(string _CUEPath, out string _DataTrackPath, out CueTrackEntry[] _Tracks, out string[] _AllTrackPaths)
	{
		_DataTrackPath = null;
		_Tracks = null;
		_AllTrackPaths = null;

		if (!PathUtility.TryReadMountedOrDataBytes(_CUEPath, out var cueBytes))
			return false;

		string cueContent = Encoding.ASCII.GetString(cueBytes);
		
		int lastSlash = _CUEPath.LastIndexOf('/');
		string cueDir = lastSlash >= 0 ? _CUEPath[..(lastSlash + 1)] : "";

		var trackEntries = new List<CueTrackEntry>();
		var trackFilePaths = new List<string>();

		string currentFile = null;
		byte currentTrack = 0;
		bool currentAudio = false;
		bool indexAdded = false;

		foreach (var rawLine in cueContent.Split('\n'))
		{
			var line = rawLine.Trim();

			if (line.StartsWith("FILE ", StringComparison.OrdinalIgnoreCase))
			{
				int q1 = line.IndexOf('"');
				int q2 = line.LastIndexOf('"');
				
				currentFile = (q1 >= 0 && q2 > q1) ? line[(q1 + 1)..q2] : null;
				
				indexAdded = false;
			}
			else if (line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
			{
				var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				
				if (parts.Length >= 3 && byte.TryParse(parts[1], out currentTrack))
				{
					currentAudio = parts[2].Equals("AUDIO", StringComparison.OrdinalIgnoreCase);
					
					indexAdded = false;
				}
			}
			else if (line.StartsWith("INDEX 01 ", StringComparison.OrdinalIgnoreCase) && !indexAdded && currentTrack > 0)
			{
				string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				
				if (parts.Length < 3 || !TryParseCueMsf(parts[2], out uint index01Lba))
					continue;

				indexAdded = true;
				
				int fileIndex = -1;
				
				if (currentFile != null)
				{
					string fullPath = (cueDir + currentFile).Replace('\\', '/');
					
					// For multi-file CUE each file line maps to one track, deduplicate for single-file CUE.
					fileIndex = trackFilePaths.FindIndex(x => x.Equals(fullPath, StringComparison.OrdinalIgnoreCase));
					
					if (fileIndex < 0)
					{
						fileIndex = trackFilePaths.Count;
						
						trackFilePaths.Add(fullPath);
					}
				}

				trackEntries.Add(new CueTrackEntry(currentTrack, currentAudio, index01Lba, Math.Max(0, fileIndex)));
			}
		}

		if (trackEntries.Count == 0 || trackFilePaths.Count == 0)
			return false;

		_AllTrackPaths = trackFilePaths.ToArray();
		_DataTrackPath = trackFilePaths[0]; // Track 1 is always the data track
		_Tracks = trackEntries.ToArray();

		return true;
	}
	
	
	
	private static bool TryParseCueMsf(string _Value, out uint _Lba)
	{
		_Lba = 0;
		
		string[] parts = _Value.Split(':');
		
		if (parts.Length != 3)
			return false;
		
		if (!uint.TryParse(parts[0], out uint minutes) ||
			!uint.TryParse(parts[1], out uint seconds) ||
			!uint.TryParse(parts[2], out uint frames))
		{
			return false;
		}
		
		if (seconds >= 60 || frames >= 75)
			return false;

		_Lba = (minutes * 60 + seconds) * 75 + frames;
		
		return true;
	}
	
	
	
	private static int ScoreEntry(LaunchEntry _Entry, string[] _PreferredFolders)
	{
		int score = 0;
		
		string lowerPath = _Entry.Path.ToLowerInvariant();
		
		foreach (var folder in _PreferredFolders)
		{
			if (lowerPath.Contains($"/{folder}/") || lowerPath.StartsWith($"{folder}/"))
				score += 10;
		}

		if (_Entry.IsBios && LooksLikeBios(_Entry.Path))
			score += 5;

		if (lowerPath.Contains("/mounted/") || _Entry.Source.Contains("Mounted", StringComparison.OrdinalIgnoreCase))
			score += 2;

		return score;
	}
	
	
	
	private static bool LooksLikeBios(string _Path)
	{
		string fileName = Path.GetFileNameWithoutExtension(_Path);
		
		return Regex.IsMatch(fileName, @"^(scph|ps-?x|bios)", RegexOptions.IgnoreCase);
	}
	
	
	
	private static bool IsValidBiosFile(string _Path)
	{
		if (!string.Equals(Path.GetExtension(_Path), ".bin", StringComparison.OrdinalIgnoreCase))
			return false;
		
		// PSX BIOS is exactly 512 KB
		// Reject without reading the file content if the size is wrong.
		long size = PathUtility.GetFileSizeBytes(PathUtility.NormalizePath(_Path));
		
		if (size >= 0 && size != PsxBiosSize)
			return false;

		if (!PathUtility.TryReadMountedOrDataBytes(_Path, out var bytes))
			return false;

		if (bytes.Length != PsxBiosSize)
			return false;

		string md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
		
		return KnownPsxBiosMd5.Contains(md5);
	}
	
	
	
	private static string BuildGameSubtitle(string _Path, string _SerialId)
	{
		if (string.IsNullOrWhiteSpace(_SerialId))
			return _Path;

		return $"{_SerialId} - {_Path}";
	}
	
	
	
	private static string TryReadDiscSerial(string _Path)
	{
		if (string.IsNullOrWhiteSpace(_Path))
			return null;

		if (!PathUtility.TryReadMountedOrDataPrefix(_Path, 1024 * 1024, out var bytes))
			return null;
		
		string text = Encoding.ASCII.GetString(bytes);
		
		var match = Regex.Match(text, @"([A-Z]{4})[_-](\d{3})\.(\d{2})", RegexOptions.IgnoreCase);
		
		if (!match.Success)
			match = Regex.Match(text, @"([A-Z]{4})[_-]?(\d{3})[_\.-]?(\d{2})", RegexOptions.IgnoreCase);

		if (!match.Success)
			return null;
		
		return $"{match.Groups[1].Value.ToUpperInvariant()}-{match.Groups[2].Value}{match.Groups[3].Value}";
	}
	
	
	
	private void TryLoadSbiForDisc(string _DiscPath)
	{
		if (string.IsNullOrWhiteSpace(_DiscPath) || Core?.Cdrom == null)
			return;
		
		// Try by game path (e.g. "Dino Crisis (France).sbi").
		string directory = Path.GetDirectoryName(_DiscPath) ?? string.Empty;
		string basename = Path.GetFileNameWithoutExtension(_DiscPath);
		string candidate1 = Path.Combine(directory, basename + ".sbi");

		if (PathUtility.TryReadMountedOrDataBytes(candidate1, out var sbiBytes))
		{
			Core.Cdrom.LoadSbi(sbiBytes);
			
			if (Core.Cdrom.HasSbi)
			{
				PsxLog.Write(PsxLogCategory.SBI, PsxLogLevel.Info, $"Loaded file from {candidate1}");
				
				return;
			}
			
			// File was present but Parse failed (bad magic, truncated, etc.).
			// Don't return, fall through to the serial-named candidate in
			// case the user has a valid SBI under a different filename.
			PsxLog.Write(PsxLogCategory.SBI, PsxLogLevel.Warn, $"File at {candidate1} failed to parse, trying alternate paths...");
		}

		// Try by game serial (e.g. "SLES_022.08.sbi").
		string serial = Core.Cdrom.GameSerial;
		
		if (!string.IsNullOrWhiteSpace(serial))
		{
			string candidate2 = Path.Combine(directory, serial + ".sbi");
			
			if (PathUtility.TryReadMountedOrDataBytes(candidate2, out sbiBytes))
			{
				Core.Cdrom.LoadSbi(sbiBytes);
				
				if (Core.Cdrom.HasSbi)
				{
					PsxLog.Write(PsxLogCategory.SBI, PsxLogLevel.Info, $"Loaded file from {candidate2} (serial match)");
					
					return;
				}
				
				PsxLog.Write(PsxLogCategory.SBI, PsxLogLevel.Warn, $"File at {candidate2} failed to parse");
			}
		}
	}
	
	
	
	// "PS-X EXE" magic at the start of a PS-X executable header.
	private static bool IsPsxExeHeader(byte[] _Header)
	{
		return _Header is { Length: >= 8 } &&
		       _Header[0] == (byte)'P' && _Header[1] == (byte)'S' && _Header[2] == (byte)'-' && _Header[3] == (byte)'X' &&
		       _Header[4] == (byte)' ' && _Header[5] == (byte)'E' && _Header[6] == (byte)'X' && _Header[7] == (byte)'E';
	}
}
