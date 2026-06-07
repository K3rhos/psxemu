namespace PSXEmu;

internal sealed class Message : IDisposable
{
	private bool m_Disposed;

	public MessageType Type { get; private init; }
	public StreamBuffer StreamBuffer { get; private init; }
	
	
	
	public static Message Create(MessageType _Type, Action<StreamBuffer> _Data = null)
	{
		var streamBuffer = new StreamBuffer();
		
		_Data?.Invoke(streamBuffer);

		return new Message
		{
			Type = _Type,
			StreamBuffer = streamBuffer
		};
	}
	
	
	
	public byte[] Serialize()
	{
		ThrowIfDisposed();

		using var serializer = new StreamBuffer();
		
		// Header
		serializer.Write((byte)Type);

		// Data
		byte[] data = StreamBuffer?.GetBytes() ?? [];
		
		serializer.Write(data.Length);
		
		if (data.Length > 0)
			serializer.Write(data);

		return serializer.GetBytes();
	}
	
	
	
	public static Message Deserialize(byte[] _Bytes)
	{
		using var serializer = new StreamBuffer(_Bytes);

		// Header
		MessageType type = (MessageType)serializer.ReadByte();
		
		// Data
		int dataLength = serializer.ReadInt32();
		
		byte[] dataBytes = dataLength > 0 ? serializer.ReadBytes(dataLength) : [];

		return new Message
		{
			Type = type,
			StreamBuffer = new StreamBuffer(dataBytes)
		};
	}
	
	
	
	void IDisposable.Dispose()
	{
		if (m_Disposed)
			return;

		m_Disposed = true;
		StreamBuffer?.Dispose();
	}
	
	
	
	private void ThrowIfDisposed()
	{
		if (!m_Disposed)
			return;

		throw new ObjectDisposedException(nameof(Message));
	}
}
