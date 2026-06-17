using System.Buffers.Binary;
using System.Text;

namespace CentrED.Server.Bridge;

public class UOWriter
{
    private byte[] _buffer;
    public int Position { get; private set; }

    public UOWriter(int capacity = 64)
    {
        _buffer = new byte[capacity];
    }

    private void Ensure(int count)
    {
        if (Position + count > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Position + count));
        }
    }

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[Position++] = value;
    }

    public void WriteSByte(sbyte value) => WriteByte((byte)value);

    public void WriteUInt16BE(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(Position), value);
        Position += 2;
    }

    public void WriteUInt16LE(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(Position), value);
        Position += 2;
    }

    public void WriteUInt32BE(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(Position), value);
        Position += 4;
    }

    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        Ensure(data.Length);
        data.CopyTo(_buffer.AsSpan(Position));
        Position += data.Length;
    }

    public void WriteZero(int count)
    {
        Ensure(count);
        _buffer.AsSpan(Position, count).Clear();
        Position += count;
    }

    public void WriteAsciiFixed(string value, int length)
    {
        Ensure(length);
        var span = _buffer.AsSpan(Position, length);
        span.Clear();
        Encoding.ASCII.GetBytes(value.AsSpan(0, Math.Min(value.Length, length - 1)), span);
        Position += length;
    }

    public void WriteAsciiNullTerminated(string value)
    {
        Ensure(value.Length + 1);
        Encoding.ASCII.GetBytes(value, _buffer.AsSpan(Position));
        Position += value.Length;
        _buffer[Position++] = 0;
    }

    public void WriteVariableHeader(byte packetId)
    {
        WriteByte(packetId);
        WriteZero(2);
    }

    public void PatchLength()
    {
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(1), (ushort)Position);
    }

    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, Position);
}
