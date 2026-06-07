using System.Threading.Tasks;

namespace PSXEmu;

public sealed partial class EmulatorComponent
{
	public async Task RefreshGameCoversAsync()
	{
		if (IsRefreshingCovers || !FetchGameCovers)
			return;

		if (_availableGames.Count == 0)
			return;

		IsRefreshingCovers = true;

		try
		{
			if (!await GetOrCreateCoverClient().EnsureConnectedAsync())
				return;

			var games = _availableGames.Select(CloneEntry).ToArray();
			
			foreach (var game in games)
			{
				byte[] coverBytes = await GetOrCreateCoverClient().RequestCoverAsync(game.SerialId);
				
				Texture coverTexture = CreateCoverTexture(coverBytes, game.SerialId);
				
				if (coverTexture.IsValid())
					SetGameCoverTexture(game.Path, coverTexture);
			}
		}
		catch (Exception _Exception)
		{
			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Warn, $"Cover WebSocket refresh failed: {_Exception.Message}");
		}
		finally
		{
			IsRefreshingCovers = false;
		}
	}
	
	
	
	private WebSocketClient GetOrCreateCoverClient()
	{
		_webSocketClient ??= CreateCoverClient();
		_webSocketClient.ConnectionUri = CoversWebSocketUri;
		_webSocketClient.UseLocalhostInEditor = UseLocalhostCoversInEditor;
		
		return _webSocketClient;
	}
	
	
	
	private WebSocketClient CreateCoverClient()
	{
		return new WebSocketClient
		{
			ConnectionUri = CoversWebSocketUri,
			UseLocalhostInEditor = UseLocalhostCoversInEditor
		};
	}
	
	
	
	private static Texture CreateCoverTexture(byte[] _Bytes, string _SerialId)
	{
		if (_Bytes == null || _Bytes.Length == 0)
			return null;

		if (_Bytes.Length > MaxCoverBytes)
		{
			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Warn, $"Ignoring oversized cover for '{_SerialId}' ({_Bytes.Length / 1024} KB).");
			
			return null;
		}

		try
		{
			using var bitmap = Bitmap.CreateFromBytes(_Bytes);
			
			return bitmap?.ToTexture(false);
		}
		catch (Exception _Exception)
		{
			string serialId = !string.IsNullOrWhiteSpace(_SerialId) ? _SerialId : "N/A";
			
			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Warn, $"Cover decode failed for '{serialId}': {_Exception.Message}");
			
			return null;
		}
	}
	
	
	
	private void SetGameCoverTexture(string _Path, Texture _CoverTexture)
	{
		for (int i = 0; i < _availableGames.Count; i++)
		{
			var entry = _availableGames[i];

			if (PathUtility.Equals(entry.Path, _Path))
			{
				_availableGames[i] = new LaunchEntry
				{
					Path = entry.Path,
					DisplayName = entry.DisplayName,
					Subtitle = entry.Subtitle,
					Source = entry.Source,
					SerialId = entry.SerialId,
					CoverTexture = _CoverTexture,
					IsBios = entry.IsBios
				};

				break;
			}
		}
	}
}
