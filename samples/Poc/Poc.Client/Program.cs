using System.Threading;
using System.Threading.Tasks;
using GnsNet;
using GnsNet.Poc;
using GnsSharp;

string address = args.Length > 0 ? args[0] : "127.0.0.1:27015";

Console.WriteLine("GNS.NET POC Client");

using GnsRuntime runtime = GnsRuntime.Initialize(new GnsRuntimeOptions
{
    DebugOutput = (level, msg) => Console.WriteLine($"[{level}] {msg}"),
});

using GnsClient client = GnsClient.Connect(address);

bool connected = false;
client.Connected += () =>
{
    connected = true;
    Console.WriteLine("Connected to server.");
};
client.Disconnected += (reason, debug) => Console.WriteLine($"Disconnected: {reason} ({debug})");

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"Connecting to {address} - press Ctrl+C to stop.");

// Wait for the connection to complete before ticking, so the first scripted inputs aren't sent
// (and dropped) before the connection is up.
while (!connected && !cts.IsCancellationRequested)
{
    await Task.Delay(16, cts.Token).ConfigureAwait(false);
}

uint lastAppliedTick = 0;
bool hasReceivedState = false;

var tickLoop = new TickLoop(Protocol.TickInterval);
await tickLoop.RunAsync((tick, _) =>
{
    (sbyte dx, sbyte dy) = ScriptedInput.At(tick);
    client.Send(new NetFrame(Protocol.OpcodeClientInput, tick, NetSerializer.Serialize(new ClientInput { Dx = dx, Dy = dy })).Encode(), ESteamNetworkingSendType.UnreliableNoDelay);

    foreach (ReceivedMessage message in client.Poll())
    {
        ServerState? state;
        try { NetFrame frame = NetFrame.Decode(message.Data); if (frame.Opcode != Protocol.OpcodeServerState || (state = NetSerializer.Deserialize<ServerState>(frame.Payload)) is null) continue; }
        catch (InvalidDataException) { continue; }
        uint serverTick = state.Tick; float x = state.X; float y = state.Y;

        // Discard any state packet whose tick isn't newer than the last one we applied - the
        // wire is unreliable and unordered, so a stale or duplicate packet can arrive at any time.
        if (hasReceivedState && !TickSequence.IsNewer(lastAppliedTick, serverTick))
        {
            continue;
        }

        lastAppliedTick = serverTick;
        hasReceivedState = true;
        Console.WriteLine($"tick={serverTick} pos=({x:F1}, {y:F1})");
    }
}, cts.Token);

Console.WriteLine("Client stopped.");
