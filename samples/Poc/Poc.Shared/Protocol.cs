namespace GnsNet.Poc;

using MemoryPack;

/// <summary>
/// The POC's shared schema and message identifiers.
/// </summary>
public static class Protocol
{
    /// <summary>Server tick rate. Both ends pace themselves off this.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50); // 20 Hz

    /// <summary>Client -&gt; Server: MemoryPack-serialized <see cref="ClientInput"/>.</summary>
    public const byte OpcodeClientInput = 0x01;

    /// <summary>Client -&gt; server: PlayFab session ticket when PlayFab mode is enabled.</summary>
    public const byte OpcodePlayFabHandshake = 0x00;

    /// <summary>Server -&gt; Client: MemoryPack-serialized <see cref="ServerState"/>.</summary>
    public const byte OpcodeServerState = 0x02;
}

[MemoryPackable]
public partial class ClientInput
{
    public sbyte Dx { get; set; }
    public sbyte Dy { get; set; }
}

[MemoryPackable]
public partial class ServerState
{
    public uint Tick { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
}
