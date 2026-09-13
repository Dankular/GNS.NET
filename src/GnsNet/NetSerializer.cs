namespace GnsNet;

using GnsSharp;
using MemoryPack;

/// <summary>MemoryPack wire serialization used by all high-level GnsNet messages.</summary>
public static class NetSerializer
{
    public static byte[] Serialize<T>(T value) where T : IMemoryPackable<T>
        => MemoryPackSerializer.Serialize(value);

    public static T? Deserialize<T>(ReadOnlySpan<byte> data) where T : IMemoryPackable<T>
        => MemoryPackSerializer.Deserialize<T>(data);
}

/// <summary>Convenience methods for sending MemoryPack messages over GNS.</summary>
public static class GnsMessageExtensions
{
    public static EResult Send<T>(this GnsClient client, T message, ESteamNetworkingSendType sendType)
        where T : IMemoryPackable<T>
        => client.Send(NetSerializer.Serialize(message), sendType);

    public static EResult Send<T>(this GnsServer server, GnsConnection connection, T message, ESteamNetworkingSendType sendType)
        where T : IMemoryPackable<T>
        => server.Send(connection, NetSerializer.Serialize(message), sendType);

    public static void Broadcast<T>(this GnsServer server, T message, ESteamNetworkingSendType sendType)
        where T : IMemoryPackable<T>
        => server.Broadcast(NetSerializer.Serialize(message), sendType);

    public static T? Read<T>(this ReceivedMessage message) where T : IMemoryPackable<T>
        => NetSerializer.Deserialize<T>(message.Data);
}
