namespace GnsNet;

using System.Buffers.Binary;

/// <summary>
/// Reads a byte buffer written by <see cref="PacketWriter"/> (big-endian throughout).
/// A ref struct so it can wrap the span of an incoming message directly without copying.
/// </summary>
public ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> buffer;
    private int position;

    public PacketReader(ReadOnlySpan<byte> buffer)
    {
        this.buffer = buffer;
        this.position = 0;
    }

    /// <summary>Bytes not yet consumed.</summary>
    public readonly int Remaining => this.buffer.Length - this.position;

    public byte ReadByte() => this.Advance(1)[0];

    public sbyte ReadSByte() => unchecked((sbyte)this.ReadByte());

    public bool ReadBool() => this.ReadByte() != 0;

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(this.Advance(2));

    public short ReadInt16() => BinaryPrimitives.ReadInt16BigEndian(this.Advance(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(this.Advance(4));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(this.Advance(8));

    public int ReadInt32() => BinaryPrimitives.ReadInt32BigEndian(this.Advance(4));

    public float ReadFloat() => BinaryPrimitives.ReadSingleBigEndian(this.Advance(4));

    public ReadOnlySpan<byte> ReadBytes(int count) => this.Advance(count);

    private ReadOnlySpan<byte> Advance(int count)
    {
        if (count < 0 || count > this.Remaining) throw new InvalidDataException("Truncated packet data.");
        ReadOnlySpan<byte> slice = this.buffer.Slice(this.position, count);
        this.position += count;
        return slice;
    }
}
