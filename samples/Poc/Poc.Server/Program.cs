using System.Threading;
using System.Threading.Tasks;
using System.Text;
using GnsNet;
using GnsNet.Poc;
using GnsSharp;

const string ListenAddress = "[::]:27015";
const float SpeedPerTick = 2f;
bool insecure = args.Any(arg => string.Equals(arg, "--insecure", StringComparison.OrdinalIgnoreCase));
bool playFab = args.Any(arg => string.Equals(arg, "--playfab", StringComparison.OrdinalIgnoreCase));
int durationSeconds = ReadDuration(args);

Console.WriteLine("GNS.NET POC Server");

using GnsRuntime runtime = GnsRuntime.Initialize(new GnsRuntimeOptions
{
    DebugOutput = (level, msg) => Console.WriteLine($"[{level}] {msg}"),
    RequireNativeAuthentication = !insecure,
});

using GnsServer server = GnsServer.Listen(ListenAddress);
if (insecure)
{
    server.SecurityPolicy = new TransportSecurityPolicy { RequireAuthenticated = false, RequireEncrypted = false };
    Console.WriteLine("WARNING: --insecure disables native authentication/encryption checks for local development only.");
}
PlayFabSessionTicketVerifier? playFabVerifier = null;
if (playFab)
{
    var playFabSettings = PlayFabEnvironment.Load();
    playFabVerifier = new PlayFabSessionTicketVerifier(playFabSettings.TitleId, playFabSettings.SecretKey);
    Console.WriteLine("PlayFab session-ticket validation enabled.");
}

server.ClientConnected += connection => Console.WriteLine($"Client connected: {connection}");
server.ClientDisconnected += (connection, reason, debug) => Console.WriteLine($"Client disconnected: {connection} ({reason}: {debug})");

using CancellationTokenSource cts = new();
if (durationSeconds > 0) cts.CancelAfter(TimeSpan.FromSeconds(durationSeconds));
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
var playFabConnections = new HashSet<GnsConnection>();

var tickLoop = new TickLoop(Protocol.TickInterval);
await tickLoop.RunAsync((tick, _) =>
{
    foreach (ReceivedMessage message in server.Poll())
    {
        try
        {
            NetFrame frame = NetFrame.Decode(message.Data);
            if (playFabVerifier is not null)
            {
                if (frame.Opcode == Protocol.OpcodePlayFabHandshake)
                {
                    ExternalIdentity? identity = playFabVerifier.VerifyAsync(Encoding.UTF8.GetString(frame.Payload)).AsTask().GetAwaiter().GetResult();
                    if (identity is null) { server.Reject(message.Connection, "Invalid PlayFab session ticket"); continue; }
                    playFabConnections.Add(message.Connection);
                    Console.WriteLine($"PlayFab authenticated: {identity.Value.Subject}");
                    continue;
                }
                if (!playFabConnections.Contains(message.Connection)) { server.Reject(message.Connection, "PlayFab handshake required"); continue; }
            }
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

static int ReadDuration(string[] arguments)
{
    for (int i = 0; i + 1 < arguments.Length; i++)
        if (string.Equals(arguments[i], "--duration-seconds", StringComparison.OrdinalIgnoreCase) && int.TryParse(arguments[i + 1], out int seconds) && seconds > 0)
            return seconds;
    return 0;
}
