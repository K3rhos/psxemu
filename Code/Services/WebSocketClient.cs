using System.Threading;
using System.Threading.Tasks;

namespace PSXEmu;

internal sealed class WebSocketClient : IDisposable
{
	private sealed class PendingCoverRequest
	{
		public TaskCompletionSource<byte[]> CompletionSource { get; init; }
	}

	private readonly SemaphoreSlim _sendLock = new(1, 1);
	private readonly Dictionary<int, PendingCoverRequest> _pendingRequests = [];
	private readonly object _pendingLock = new();
	private WebSocket _socket;
	private int _nextRequestId;

	public string ConnectionUri { get; set; } = "ws://localhost:8080/";
	public bool UseLocalhostInEditor { get; set; } = true;
	public float ConnectionTimeoutSeconds { get; set; } = 5f;

	public bool IsConnected => _socket is { IsConnected: true };

	public async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken = default)
	{
		if (IsConnected)
			return true;

		DisposeSocket();

		_socket = new WebSocket(262144);
		_socket.OnDataReceived += HandleDataReceived;
		_socket.OnDisconnected += HandleDisconnected;

		string connectionUri = ResolveConnectionUri();
		TimeSpan timeout = TimeSpan.FromSeconds(MathF.Max(1f, ConnectionTimeoutSeconds));

		using var timeoutCts = new CancellationTokenSource(timeout);
		using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

		try
		{
			await _socket.Connect(connectionUri, combinedCts.Token);
			return true;
		}
		catch (Exception ex)
		{
			Log.Warning($"Cover WebSocket connect failed ({connectionUri}): {ex.Message}");
			DisposeSocket();
			FailPendingRequests("Cover WebSocket is disconnected.");
			return false;
		}
	}

	public async Task<byte[]> RequestCoverAsync(string serialId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(serialId))
			return null;

		if (!await EnsureConnectedAsync(cancellationToken))
			return null;

		int requestId = Interlocked.Increment(ref _nextRequestId);
		var completionSource = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

		lock (_pendingLock)
		{
			_pendingRequests[requestId] = new PendingCoverRequest
			{
				CompletionSource = completionSource
			};
		}

		using var registration = cancellationToken.Register(() => CancelPendingRequest(requestId, cancellationToken));

		try
		{
			await SendMessageAsync(MessageType.CoverRequest, stream =>
			{
				stream.Write(requestId);
				stream.Write(serialId ?? string.Empty);
			});

			return await completionSource.Task;
		}
		catch
		{
			lock (_pendingLock)
			{
				_pendingRequests.Remove(requestId);
			}

			throw;
		}
	}

	private async Task SendMessageAsync(MessageType messageType, Action<StreamBuffer> writeData)
	{
		if (_socket == null || !_socket.IsConnected)
			return;

		await _sendLock.WaitAsync();
		try
		{
			using var message = Message.Create(messageType, writeData);
			await _socket.Send(message.Serialize());
		}
		finally
		{
			_sendLock.Release();
		}
	}

	private void HandleDataReceived(Span<byte> data)
	{
		using var message = Message.Deserialize(data.ToArray());
		
		if (message.Type != MessageType.CoverResponse)
			return;

		StreamBuffer stream = message.StreamBuffer;
		int requestId = stream.ReadInt32();
		bool found = stream.ReadBoolean();
		int byteCount = stream.ReadInt32();
		byte[] coverBytes = byteCount > 0 ? stream.ReadBytes(byteCount) : null;

		TaskCompletionSource<byte[]> completionSource = null;
		lock (_pendingLock)
		{
			if (_pendingRequests.TryGetValue(requestId, out PendingCoverRequest pendingRequest))
			{
				completionSource = pendingRequest.CompletionSource;
				_pendingRequests.Remove(requestId);
			}
		}

		completionSource?.TrySetResult(found ? coverBytes : null);
	}

	private void HandleDisconnected(int status, string reason)
	{
		Log.Info($"Cover WebSocket disconnected ({status}): {reason}");
		DisposeSocket();
		FailPendingRequests($"Cover WebSocket disconnected: {reason}");
	}

	private void CancelPendingRequest(int requestId, CancellationToken cancellationToken)
	{
		lock (_pendingLock)
		{
			if (_pendingRequests.TryGetValue(requestId, out PendingCoverRequest pendingRequest))
			{
				_pendingRequests.Remove(requestId);
				pendingRequest.CompletionSource.TrySetCanceled(cancellationToken);
			}
		}
	}

	private void FailPendingRequests(string reason)
	{
		List<TaskCompletionSource<byte[]>> completions = [];
		lock (_pendingLock)
		{
			foreach (PendingCoverRequest pendingRequest in _pendingRequests.Values)
				completions.Add(pendingRequest.CompletionSource);

			_pendingRequests.Clear();
		}

		foreach (TaskCompletionSource<byte[]> completionSource in completions)
			completionSource.TrySetException(new InvalidOperationException(reason));
	}

	private string ResolveConnectionUri()
	{
		if (UseLocalhostInEditor && Game.IsEditor)
			return "ws://localhost:8080/";

		return string.IsNullOrWhiteSpace(ConnectionUri) ? "ws://localhost:8080/" : ConnectionUri;
	}

	private void DisposeSocket()
	{
		if (_socket == null)
			return;

		_socket.OnDataReceived -= HandleDataReceived;
		_socket.OnDisconnected -= HandleDisconnected;
		_socket.Dispose();
		_socket = null;
	}

	public void Dispose()
	{
		DisposeSocket();
		FailPendingRequests("Cover WebSocket client disposed.");
		_sendLock.Dispose();
	}
}
