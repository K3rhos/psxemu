using System.Text.RegularExpressions;

namespace PSXEmu;

public static class PathUtility
{
	public static string NormalizePath(string _Path) => _Path.Replace('\\', '/').TrimStart('/');

	public static bool Equals(string _PathA, string _PathB) => string.Equals(NormalizePath(_PathA ?? string.Empty), NormalizePath(_PathB ?? string.Empty), StringComparison.OrdinalIgnoreCase);
	
	
	
	public static long GetFileSizeBytes(string _Path)
	{
		if (FileSystem.Mounted.FileExists(_Path))
		{
			using var stream = FileSystem.Mounted.OpenRead(_Path);
			
			if (stream != null)
				return stream.Length;
		}

		if (FileSystem.Data.FileExists(_Path))
		{
			using var stream = FileSystem.Data.OpenRead(_Path);
			
			if (stream != null)
				return stream.Length;
		}
		
		return -1;
	}
	
	
	
	public static string GetDisplayName(string _Path, bool _ToUpper)
	{
		string fileName = System.IO.Path.GetFileNameWithoutExtension(_Path).Replace('_', ' ');
		
		fileName = Regex.Replace(fileName, @"\s+", " ").Trim();
		
		if (_ToUpper)
			return fileName.ToUpperInvariant();

		return fileName;
	}
	
	
	
	public static bool TryReadMountedOrDataBytes(string _Path, out byte[] _Bytes)
	{
		string normalizedPath = NormalizePath(_Path);
		
		if (FileSystem.Mounted.FileExists(normalizedPath))
		{
			_Bytes = FileSystem.Mounted.ReadAllBytes(normalizedPath).ToArray();
			
			return true;
		}
		
		if (FileSystem.Data.FileExists(normalizedPath))
		{
			_Bytes = FileSystem.Data.ReadAllBytes(normalizedPath).ToArray();
			
			return true;
		}
		
		_Bytes = null;

		return false;
	}
	
	
	
	public static bool TryReadMountedOrDataPrefix(string _Path, int _MaxBytes, out byte[] _Bytes)
	{
		_Bytes = null;
		
		string normalizedPath = NormalizePath(_Path);

		if (TryReadPrefix(FileSystem.Mounted, normalizedPath, _MaxBytes, out _Bytes))
			return true;

		return TryReadPrefix(FileSystem.Data, normalizedPath, _MaxBytes, out _Bytes);
	}
	
	
	
	public static bool TryReadPrefix(BaseFileSystem _FileSystem, string _Path, int _MaxBytes, out byte[] _Bytes)
	{
		_Bytes = null;
		
		if (_FileSystem == null || !_FileSystem.FileExists(_Path))
			return false;

		using var stream = _FileSystem.OpenRead(_Path);
		
		if (stream == null)
			return false;

		int length = int.Min(int.Max(_MaxBytes, 0), (int)stream.Length);
		
		_Bytes = new byte[length];
		
		int offset = 0;
		
		while (offset < length)
		{
			int read = stream.Read(_Bytes, offset, length - offset);
			
			if (read <= 0)
				break;

			offset += read;
		}
		
		if (offset == length)
			return true;
		
		Array.Resize(ref _Bytes, offset);
		
		return offset > 0;
	}
	
	
	
	public static IEnumerable<(string Name, BaseFileSystem FileSystem)> GetSearchFileSystems()
	{
		yield return ("Mounted", FileSystem.Mounted);
		yield return ("Data", FileSystem.Data);
	}
	
	
	
	public static IEnumerable<string> EnumerateFilePaths(BaseFileSystem _FileSystem)
	{
		if (_FileSystem == null)
			yield break;
		
		foreach (var path in _FileSystem.FindFile("/", "*", true))
			yield return path;
	}
	
	
	
	public static string SanitizeFileName(string _FileName)
	{
		var sb = new System.Text.StringBuilder(_FileName.Length);
		
		foreach (char c in _FileName)
			sb.Append((char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ' || c == '(' || c == ')') ? c : '_');
		
		return sb.ToString().Trim();
	}
}
