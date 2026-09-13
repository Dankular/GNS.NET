namespace GnsNet.Tests;

using GnsNet;
using Xunit;

public class PacketReaderWriterTests
{
    [Fact]
    public void RoundTrips_AllSupportedTypes()
    {
        var writer = new PacketWriter();
        writer.WriteByte(0x02);
        writer.WriteSByte(-5);
        writer.WriteBool(true);
        writer.WriteUInt16(60000);
        writer.WriteInt16(-1234);
        writer.WriteUInt32(4_000_000_000);
        writer.WriteInt32(-70000);
        writer.WriteFloat(3.14159f);
        writer.WriteBytes([1, 2, 3, 4]);

        var reader = new PacketReader(writer.WrittenSpan);
        Assert.Equal(0x02, reader.ReadByte());
        Assert.Equal(-5, reader.ReadSByte());
        Assert.True(reader.ReadBool());
        Assert.Equal(60000, reader.ReadUInt16());
        Assert.Equal(-1234, reader.ReadInt16());
        Assert.Equal(4_000_000_000u, reader.ReadUInt32());
        Assert.Equal(-70000, reader.ReadInt32());
        Assert.Equal(3.14159f, reader.ReadFloat());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, reader.ReadBytes(4).ToArray());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Writer_GrowsPastInitialCapacity()
    {
        var writer = new PacketWriter(initialCapacity: 2);
        for (int i = 0; i < 100; i++)
        {
            writer.WriteInt32(i);
        }

        Assert.Equal(400, writer.Length);

        var reader = new PacketReader(writer.WrittenSpan);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(i, reader.ReadInt32());
        }
    }

    [Fact]
    public void Reset_ClearsWrittenLength()
    {
        var writer = new PacketWriter();
        writer.WriteUInt32(42);
        Assert.Equal(4, writer.Length);

        writer.Reset();
        Assert.Equal(0, writer.Length);
        Assert.Empty(writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void MatchesThePocWireFormat_ClientInput()
    {
        // [0x01][sbyte dx][sbyte dy]
        var writer = new PacketWriter();
        writer.WriteByte(0x01);
        writer.WriteSByte(-1);
        writer.WriteSByte(1);

        Assert.Equal(new byte[] { 0x01, unchecked((byte)-1), 0x01 }, writer.WrittenSpan.ToArray());
    }
}
