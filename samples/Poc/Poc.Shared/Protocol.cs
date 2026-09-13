namespace GnsNet.Poc;

/// <summary>
/// The POC's hand-packed wire protocol. Intentionally minimal - see samples/Poc/README.md.
/// </summary>
public static class Protocol
{
    /// <summary>Server tick rate. Both ends pace themselves off this.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(50); // 20 Hz

    /// <summary>Client -&gt; Server: [Opcode.ClientInput][sbyte dx][sbyte dy].</summary>
    public const byte OpcodeClientInput = 0x01;

    /// <summary>Server -&gt; Client: [Opcode.ServerState][uint32 tick][float x][float y].</summary>
    public const byte OpcodeServerState = 0x02;
}
