using System.Threading;
using System.Threading.Tasks;
using GnsNet;
using GnsNet.Poc;
using GnsSharp;

const string ListenAddress = "[::]:27015";
const float SpeedPerTick = 2f;

Console.WriteLine("GNS.NET POC Server");

using GnsRuntime runtime = GnsRuntime.Initialize(new GnsRuntimeOptions
{
    DebugOutput = (level, msg) => Console.WriteLine($"[{level}] {msg}"),
});

using GnsServer server = GnsServer.Listen(ListenAddress);

server.ClientConnected += connection => Console.WriteLine($"Client connected: {connection}");
server.ClientDisconnected += (connection, reason, debug) => Console.WriteLine($"Client disconnected: {connection} ({reason}: {debug})");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"Listening on {ListenAddress} - press Ctrl+C to stop.");

// The one entity this POC's server is authoritative over.
float x = 0f;
float y = 0f;

// Last input received from the (single, unauthenticated) client. Applied every tick regardless
// of when during the tick it arrived - this POC does not timestamp or buffer input by tick.
sbyte inputDx = 0;
sbyte inputDy = 0;

var tickLoop = new TickLoop(Protocol.TickInterval);
await tickLoop.RunAsync((tick, _) =>
{
    foreach (ReceivedMessage message in server.Poll())
    {
        var reader = new PacketReader(message.Data);
        if (reader.Remaining < 1)
        {
            continue;
        }

        byte opcode = reader.ReadByte();
        if (opcode == Protocol.OpcodeClientInput && reader.Remaining >= 2)
        {
            inputDx = reader.ReadSByte();
            inputDy = reader.ReadSByte();
        }
    }

    x += inputDx * SpeedPerTick;
    y += inputDy * SpeedPerTick;

    var writer = new PacketWriter();
    writer.WriteByte(Protocol.OpcodeServerState);
    writer.WriteUInt32(tick);
    writer.WriteFloat(x);
    writer.WriteFloat(y);
    server.Broadcast(writer.WrittenSpan, ESteamNetworkingSendType.UnreliableNoDelay);
}, cts.Token);

Console.WriteLine("Server stopped.");
