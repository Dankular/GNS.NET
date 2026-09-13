namespace GnsNet;

using System.Buffers.Binary;

/// <summary>
/// A small growable byte buffer writer for hand-packed wire messages, using big-endian
/// ("network order") encoding throughout. This is deliberately not a general-purpose
/// serialization framework - it's the same kind of manual byte packing the original POC
/// did inline, pulled out so every game project built on GnsNet doesn't reinvent it.
/// </summary>
public sealed class PacketWriter
{
    private byte[] buffer;
    private int length;

    public PacketWriter(int initialCapacity = 64)
    {
        this.buffer = new byte[Math.Max(initialCapacity, 1)];
    }

    /// <summary>Number of bytes written so far.</summary>
    public int Length => this.length;

    /// <summary>The bytes written so far.</summary>
    public ReadOnlySpan<byte> WrittenSpan => this.buffer.AsSpan(0, this.length);

    /// <summary>Resets the writer to empty without releasing the underlying buffer.</summary>
    public void Reset() => this.length = 0;

    public void WriteByte(byte value) => this.Reserve(1)[0] = value;

    public void WriteSByte(sbyte value) => this.Reserve(1)[0] = unchecked((byte)value);

    public void WriteBool(bool value) => this.WriteByte(value ? (byte)1 : (byte)0);

    public void WriteUInt16(ushort value) => BinaryPrimitives.WriteUInt16BigEndian(this.Reserve(2), value);

    public void WriteInt16(short value) => BinaryPrimitives.WriteInt16BigEndian(this.Reserve(2), value);

    public void WriteUInt32(uint value) => BinaryPrimitives.WriteUInt32BigEndian(this.Reserve(4), value);

    public void WriteUInt64(ulong value) => BinaryPrimitives.WriteUInt64BigEndian(this.Reserve(8), value);

    public void WriteInt32(int value) => BinaryPrimitives.WriteInt32BigEndian(this.Reserve(4), value);

    public void WriteFloat(float value) => BinaryPrimitives.WriteSingleBigEndian(this.Reserve(4), value);

    public void WriteBytes(ReadOnlySpan<byte> data) => data.CopyTo(this.Reserve(data.Length));

    private Span<byte> Reserve(int count)
    {
        this.EnsureCapacity(count);
        Span<byte> span = this.buffer.AsSpan(this.length, count);
        this.length += count;
        return span;
    }

    private void EnsureCapacity(int additional)
    {
        int required = this.length + additional;
        if (required <= this.buffer.Length)
        {
            return;
        }

        int newCapacity = this.buffer.Length * 2;
        while (newCapacity < required)
        {
            newCapacity *= 2;
        }

        Array.Resize(ref this.buffer, newCapacity);
    }
}
