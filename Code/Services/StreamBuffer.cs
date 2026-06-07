using System.IO;

namespace PSXEmu;

internal sealed class StreamBuffer : IDisposable
{
	private readonly MemoryStream m_Stream;
	private readonly BinaryWriter m_Writer;
	private readonly BinaryReader m_Reader;



	public StreamBuffer(byte[] _Data = null)
	{
		m_Stream = _Data != null ? new MemoryStream(_Data) : new MemoryStream();
		m_Writer = new BinaryWriter(m_Stream);
		m_Reader = new BinaryReader(m_Stream);
	}



	// Write methods
	public void Write(byte[] _Data) => m_Writer.Write(_Data, 0, _Data.Length);
	public void Write(byte _Value) => m_Writer.Write(_Value);
	public void Write(short _Value) => m_Writer.Write(_Value);
	public void Write(int _Value) => m_Writer.Write(_Value);
	public void Write(long _Value) => m_Writer.Write(_Value);
	public void Write(float _Value) => m_Writer.Write(_Value);
	public void Write(double _Value) => m_Writer.Write(_Value);
	public void Write(bool _Value) => m_Writer.Write(_Value);
	public void Write(string _Value) => m_Writer.Write(_Value ?? "");
	
	
	
	// Read methods
	public byte[] ReadBytes(int _Count) => m_Reader.ReadBytes(_Count);
	public byte ReadByte() => m_Reader.ReadByte();
	public short ReadInt16() => m_Reader.ReadInt16();
	public int ReadInt32() => m_Reader.ReadInt32();
	public long ReadInt64() => m_Reader.ReadInt64();
	public float ReadSingle() => m_Reader.ReadSingle();
	public double ReadDouble() => m_Reader.ReadDouble();
	public bool ReadBoolean() => m_Reader.ReadBoolean();
	public string ReadString() => m_Reader.ReadString();
	
	
	
	public byte[] GetBytes() => m_Stream.ToArray();
	public int Length() => (int)m_Stream.Length;
	public void Reset() => m_Stream.Position = 0;
	
	
	
	public void Dispose()
	{
		m_Writer?.Dispose();
		m_Reader?.Dispose();
		m_Stream?.Dispose();
	}
}
