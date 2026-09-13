namespace GnsNet;

using GnsSharp;

/// <summary>Batches multiple frames into one GNS message for a simulation tick.</summary>
public sealed class NetBatch
{
    public const byte Magic = 0xB7;
    public const int MaxFrames = 1024;
    public const int MaxBatchBytes = 8 * 1024 * 1024;
    private readonly List<NetFrame> frames = new();
    public int Count => this.frames.Count;
    public void Add(NetFrame frame) => this.frames.Add(frame);
    public byte[] Encode()
    {
        var writer = new PacketWriter();
        writer.WriteByte(Magic);
        if (this.frames.Count > MaxFrames) throw new InvalidOperationException("Network batch contains too many frames.");
        writer.WriteUInt16((ushort)this.frames.Count);
        foreach (NetFrame frame in this.frames)
        {
            byte[] data = frame.Encode();
            writer.WriteUInt32((uint)data.Length);
            writer.WriteBytes(data);
        }
        if (writer.WrittenSpan.Length > MaxBatchBytes) throw new InvalidOperationException("Network batch is too large.");
        return writer.WrittenSpan.ToArray();
    }
    public static IReadOnlyList<NetFrame> Decode(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length > MaxBatchBytes) throw new InvalidDataException("Network batch is too large.");
            var reader = new PacketReader(data);
            if (reader.ReadByte() != Magic) throw new InvalidDataException("Invalid network batch marker.");
            int count = reader.ReadUInt16();
            if (count > MaxFrames) throw new InvalidDataException("Network batch contains too many frames.");
            var result = new List<NetFrame>(count);
            for (int i = 0; i < count; i++) result.Add(NetFrame.Decode(reader.ReadBytes(checked((int)reader.ReadUInt32()))));
            if (reader.Remaining != 0) throw new InvalidDataException("Trailing bytes in network batch.");
            return result;
        }
        catch (ArgumentException exception) { throw new InvalidDataException("Malformed network batch.", exception); }
        catch (OverflowException exception) { throw new InvalidDataException("Malformed network batch.", exception); }
    }
}

public enum NetChannel { State, Event }

public static class NetChannels
{
    public static ESteamNetworkingSendType SendType(this NetChannel channel)
        => channel == NetChannel.Event ? ESteamNetworkingSendType.Reliable : ESteamNetworkingSendType.UnreliableNoDelay;
}
