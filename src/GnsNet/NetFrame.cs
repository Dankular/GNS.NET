namespace GnsNet;

using MemoryPack;

/// <summary>Small common envelope used by the high-level framework.</summary>
public readonly record struct NetFrame(byte Opcode, uint Tick, byte[] Payload)
{
    public const byte CurrentVersion = 1;
    public const byte CurrentSchemaVersion = 1;
    public const int MaxPayloadBytes = 1024 * 1024;
    /// <summary>MemoryPack schema revision carried by this frame.</summary>
    public byte SchemaVersion { get; init; } = CurrentSchemaVersion;
    public byte[] Encode()
    {
        if (Payload is null || Payload.Length > MaxPayloadBytes) throw new ArgumentException("Network frame payload is too large.", nameof(Payload));
        if (SchemaVersion != CurrentSchemaVersion) throw new InvalidOperationException($"Unsupported schema version {SchemaVersion}.");
        var writer = new PacketWriter(Payload.Length + 6);
        writer.WriteByte(CurrentVersion);
        writer.WriteByte(SchemaVersion);
        writer.WriteByte(Opcode);
        writer.WriteUInt32(Tick);
        writer.WriteBytes(Payload);
        return writer.WrittenSpan.ToArray();
    }
    public static NetFrame Decode(ReadOnlySpan<byte> data)
    {
        var reader = new PacketReader(data);
        if (reader.ReadByte() != CurrentVersion) throw new InvalidDataException("Unsupported network protocol version.");
        if (reader.ReadByte() != CurrentSchemaVersion) throw new InvalidDataException("Unsupported network schema version.");
        byte opcode = reader.ReadByte();
        uint tick = reader.ReadUInt32();
        if (reader.Remaining > MaxPayloadBytes) throw new InvalidDataException("Network frame payload is too large.");
        return new NetFrame(opcode, tick, reader.ReadBytes(reader.Remaining).ToArray());
    }
}

/// <summary>Routes framed messages to typed MemoryPack handlers.</summary>
public sealed class NetMessageRouter
{
    private readonly Dictionary<byte, Func<NetFrame, object?>> handlers = new();

    public void Register<T>(byte opcode, Action<T, NetFrame> handler) where T : IMemoryPackable<T>
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!this.handlers.TryAdd(opcode, frame =>
        {
            T? message;
            try { message = NetSerializer.Deserialize<T>(frame.Payload); }
            catch (Exception) { return null; }
            if (message is not null) handler(message, frame);
            return message;
        })) throw new InvalidOperationException($"Opcode {opcode} is already registered.");
    }

    public bool Dispatch(NetFrame frame)
        => this.handlers.TryGetValue(frame.Opcode, out Func<NetFrame, object?>? handler)
           && handler(frame) is not null;
}
