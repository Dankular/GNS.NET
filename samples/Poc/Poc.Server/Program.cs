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

// The simulation owns truth; the client can submit only bounded directional input.
var inputGuard = new ServerInputGuard<string, ClientInput>((_, value) => value.Dx is >= -1 and <= 1 && value.Dy is >= -1 and <= 1);
var simulation = new AuthoritativeServer<string, ServerState, ClientInput>(new ServerState(),
    (state, _, value) => new ServerState { X = state.X + value.Dx * SpeedPerTick, Y = state.Y + value.Dy * SpeedPerTick },
    inputGuard: inputGuard);
simulation.AddClient("poc-client");

var tickLoop = new TickLoop(Protocol.TickInterval);
await tickLoop.RunAsync((tick, _) =>
{
    foreach (ReceivedMessage message in server.Poll())
    {
        try
        {
            NetFrame frame = NetFrame.Decode(message.Data);
            if (frame.Opcode == Protocol.OpcodeClientInput && NetSerializer.Deserialize<ClientInput>(frame.Payload) is ClientInput input)
                simulation.SubmitInput("poc-client", frame.Tick, input);
        }
        catch (InvalidDataException) { }
    }

    simulation.Advance();
    ServerState state = simulation.State;
    server.Broadcast(new NetFrame(Protocol.OpcodeServerState, tick, NetSerializer.Serialize(new ServerState { Tick = tick, X = state.X, Y = state.Y })).Encode(), ESteamNetworkingSendType.UnreliableNoDelay);
}, cts.Token);

Console.WriteLine("Server stopped.");
