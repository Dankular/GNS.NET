using System.Diagnostics;
using System.Buffers.Binary;
using GnsNet;
using GnsSharp;
using MemoryPack;

BenchmarkOptions options = BenchmarkOptions.Parse(args);
var benchmarkResults = new List<object>();
Console.WriteLine($"GNS.NET benchmark | scenario={options.Scenario} clients={options.Clients} entities={options.Entities} iterations={options.Iterations}");
Console.WriteLine($"Runtime: {Environment.Version} | CPU threads: {Environment.ProcessorCount}");
if (options.Scenario is "all" or "serialize") Run("MemoryPack serialize + deserialize", options, BenchmarkSerialization);
if (options.Scenario is "all" or "stress") Run("concurrent client serialization stress", options, BenchmarkConcurrentSerialization);
if (options.Scenario is "transport") RunTransport(options);
if (options.Scenario is "authenticated-transport") RunAuthenticatedTransport(options);
if (options.Scenario is "p2p") await RunP2P(options);
if (options.Scenario is "saturation") RunSaturation(options);
if (options.Scenario is "playfab") await RunPlayFab(options);
if (options.Scenario is "all" or "batch") Run("NetBatch encode + decode", options, BenchmarkBatch);
if (options.Scenario is "all" or "pipeline") Run("AOI + delta + priority snapshot pipeline", options, BenchmarkPipeline);
if (options.Scenario is "all" or "prediction") Run("client prediction reconciliation", options, BenchmarkPrediction);
if (options.Scenario is "all" or "replay") Run("persisted capture replay", options, BenchmarkReplay);
if (options.JsonPath is not null && options.Scenario is not "saturation")
{
    object document = options.Deterministic
        ? new { schema = 1, deterministic = true, scenario = options.Scenario, clients = options.Clients, entities = options.Entities, iterations = options.Iterations, results = benchmarkResults }
        : new { schema = 1, generatedAt = DateTimeOffset.UtcNow, scenario = options.Scenario, clients = options.Clients, entities = options.Entities, iterations = options.Iterations, results = benchmarkResults };
    File.WriteAllText(options.JsonPath, System.Text.Json.JsonSerializer.Serialize(document, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Benchmark JSON: {options.JsonPath}");
}

if (options.Scenario == "matrix") RunMatrix(options);
if (options.Scenario == "native-matrix") RunNativeMatrix(options);

void Run(string name, BenchmarkOptions options, Func<BenchmarkOptions, BenchmarkResult> benchmark)
{
    BenchmarkResult result = benchmark(options);
    benchmarkResults.Add(new { name, result.Operations, result.Bytes, elapsedMs = options.Deterministic ? 0 : result.Elapsed.TotalMilliseconds, allocated = options.Deterministic ? 0 : result.Allocated, opsPerSecond = options.Deterministic ? 0 : result.Operations / Math.Max(result.Elapsed.TotalSeconds, double.Epsilon), fingerprint = result.Fingerprint });
    Console.WriteLine($"{name}: {result.Operations:N0} ops | {result.Operations / result.Elapsed.TotalSeconds:N0} ops/s | {result.Bytes / 1024d / 1024d:N2} MiB processed | {result.Allocated / (double)Math.Max(1, result.Operations):N1} B/op | {result.Elapsed.TotalMilliseconds:N1} ms");
}

void RunMatrix(BenchmarkOptions options)
{
    var profiles = new List<MatrixProfileResult>();
    int[] clients = [1, options.Clients];
    int[] entities = [Math.Min(32, options.Entities), options.Entities];
    int[] payloads = [Math.Min(32, options.PayloadBytes), options.PayloadBytes];
    foreach (int clientCount in clients.Distinct())
        foreach (int entityCount in entities.Distinct())
            foreach (int payloadBytes in payloads.Distinct())
                foreach (double lossPercent in new[] { 0d, 10d })
                    foreach (bool reconnect in new[] { false, true })
                    {
                        MatrixProfile profile = new(clientCount, entityCount, Math.Max(1, payloadBytes), lossPercent, reconnect);
                        MatrixProfileResult result = BenchmarkMatrixProfile(profile, options.Iterations);
                        profiles.Add(result);
                        Console.WriteLine($"Matrix clients={clientCount} entities={entityCount} payload={profile.PayloadBytes} loss={lossPercent:0}% reconnect={reconnect}: frames={result.Frames:N0} replayed={result.ReplayedLifecycleRecords:N0} delivered={result.DeliveredPackets:N0}/{result.CapturedPackets:N0}");
                    }
    if (options.JsonPath is not null)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { schema = 1, deterministic = options.Deterministic, scenario = "matrix", iterations = options.Iterations, profiles }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(options.JsonPath, json);
        Console.WriteLine($"Matrix JSON: {options.JsonPath}");
    }
}

static MatrixProfileResult BenchmarkMatrixProfile(MatrixProfile profile, int iterations)
{
    var room = new RoomLifecycle<string>(Math.Max(2, profile.Clients)) { AllowLateJoin = true };
    for (int client = 0; client < profile.Clients; client++) { string id = $"client-{client}"; room.Join(id); room.SetReady(id, true); }
    if (room.Phase == RoomPhase.Ready) { room.Start(); room.BeginGame(); }
    List<BenchmarkEntity> entities = Enumerable.Range(0, profile.Entities).Select(i => new BenchmarkEntity(i, i % 100, i % 70)).ToList();
    var interest = new InterestManager<string, BenchmarkEntity>();
    var delta = new DeltaCompressor<BenchmarkSnapshot>((_, current) => current, (_, current) => current);
    var pipeline = new SnapshotPipeline<string, BenchmarkEntity, BenchmarkSnapshot>(interest, delta, entity => (entity.X, entity.Y));
    for (int client = 0; client < profile.Clients; client++) interest.SetView($"client-{client}", new(50, 35, 100));
    var conditions = new NetworkConditionSimulator(new NetworkConditions(TimeSpan.Zero, TimeSpan.Zero, profile.LossPercent), seed: 17);
    var recorder = new NetworkRecorder();
    int frames = 0, captured = 0, delivered = 0, replayed = 0;
    if (profile.Reconnect)
    {
        var authoritative = new NetworkObjectRegistry<string>(); var clientObjects = new NetworkObjectRegistry<string>();
        var rehydration = new SessionRehydrationBuffer<string>(Math.Max(16, profile.Entities * 2));
        NetworkObjectDescriptor objectRecord = authoritative.Spawn(1, "client-0", 1);
        rehydration.Record("client-0", new NetworkObjectChange(NetworkObjectChangeKind.Spawned, objectRecord));
        replayed = rehydration.Replay("client-0", clientObjects);
    }
    for (uint tick = 1; tick <= iterations; tick++)
        for (int client = 0; client < profile.Clients; client++)
        {
            string id = $"client-{client}";
            pipeline.Queue(id, entities, new BenchmarkSnapshot(tick, client, entities.Count), 1, 2, tick, 1f);
            foreach ((NetFrame Frame, NetChannel Channel) item in pipeline.Drain(id, 4_096))
            {
                byte[] payload = item.Frame.Payload.Length >= profile.PayloadBytes ? item.Frame.Payload : item.Frame.Payload.Concat(new byte[profile.PayloadBytes - item.Frame.Payload.Length]).ToArray();
                recorder.Record(true, payload, DateTimeOffset.UnixEpoch.AddTicks(frames), id, item.Channel);
                captured++; frames++; if (!conditions.ShouldDrop()) delivered++;
            }
            pipeline.Acknowledge(id, new BenchmarkSnapshot(tick, client, entities.Count));
        }
    int replayDelivered = recorder.ReplayTransportAsync(_ => ValueTask.FromResult(true), speed: double.MaxValue).GetAwaiter().GetResult();
    return new(profile, frames, captured, Math.Min(delivered, replayDelivered), replayed);
}

static void RunTransport(BenchmarkOptions options)
{
    if (!options.Insecure) throw new InvalidOperationException("The local transport benchmark is intentionally unauthenticated; pass --insecure explicitly.");
    try
    {
        TransportResult result = BenchmarkTransport(options, 1);
        Console.WriteLine($"GNS loopback transport: {result.Messages:N0} echoed | {result.Messages / result.Elapsed.TotalSeconds:N0} messages/s | {result.Bytes / 1024d / 1024d:N2} MiB on wire | RTT p50={result.P50.TotalMilliseconds:N2} ms p99={result.P99.TotalMilliseconds:N2} ms | loss={result.Loss:P2} | sequences={result.UniqueSequences:N0}/{result.SentSequences:N0} unique | duplicates={result.DuplicateSequences:N0} missing={result.MissingSequences:N0} | connect p50={result.ConnectP50.TotalMilliseconds:N2} ms");
    }
    catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
    {
        Console.WriteLine($"GNS loopback transport: unavailable ({exception.Message})");
        Console.WriteLine("Build or provide the native GameNetworkingSockets library, then pass --native-path <path>.");
    }
}

static void RunNativeMatrix(BenchmarkOptions options)
{
    if (!options.Insecure) throw new InvalidOperationException("The native matrix is intentionally unauthenticated; pass --insecure explicitly.");
    int[] clients = [1, options.Clients];
    int[] payloads = [Math.Min(32, Math.Max(1, options.PayloadBytes)), Math.Max(1, options.PayloadBytes)];
    var profiles = new List<NativeMatrixProfileResult>();
    foreach (int clientCount in clients.Distinct())
        foreach (int payloadBytes in payloads.Distinct())
            foreach (int lossPercent in new[] { 0, 10 })
                foreach (bool reconnect in new[] { false, true })
                {
                    BenchmarkOptions profile = options with { Clients = clientCount, PayloadBytes = payloadBytes, NativeLossPercent = lossPercent };
                    TransportResult first = BenchmarkTransport(profile, 1);
                    TransportResult second = reconnect ? BenchmarkTransport(profile, 1) : default;
                    NativeMatrixProfile matrix = new(clientCount, payloadBytes, lossPercent, reconnect);
                    NativeMatrixProfileResult result = new(matrix, first.SentSequences + second.SentSequences, first.UniqueSequences + second.UniqueSequences, first.MissingSequences + second.MissingSequences, first.DuplicateSequences + second.DuplicateSequences, first.Bytes + second.Bytes);
                    profiles.Add(result);
                    Console.WriteLine($"Native matrix clients={clientCount} payload={payloadBytes} loss={lossPercent}% reconnect={reconnect}: unique={result.UniqueSequences:N0}/{result.SentSequences:N0} missing={result.MissingSequences:N0} duplicates={result.DuplicateSequences:N0}");
                }
    if (options.JsonPath is not null)
    {
        object document = new { schema = 1, deterministic = options.Deterministic, scenario = "native-matrix", clients = options.Clients, payloadBytes = options.PayloadBytes, iterations = options.Iterations, profiles };
        File.WriteAllText(options.JsonPath, System.Text.Json.JsonSerializer.Serialize(document, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Native matrix JSON: {options.JsonPath}");
    }
}

static void RunAuthenticatedTransport(BenchmarkOptions options)
{
    try
    {
        TransportResult result = BenchmarkTransport(options with { Insecure = true }, 1, authenticated: true);
        Console.WriteLine($"GNS authenticated loopback transport: {result.Messages:N0} echoed | {result.Messages / result.Elapsed.TotalSeconds:N0} messages/s | RTT p50={result.P50.TotalMilliseconds:N2} ms p99={result.P99.TotalMilliseconds:N2} ms | sequences={result.UniqueSequences:N0}/{result.SentSequences:N0} unique | connect p50={result.ConnectP50.TotalMilliseconds:N2} ms");
    }
    catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or NotSupportedException or TimeoutException)
    {
        throw new InvalidOperationException($"GNS authenticated loopback transport is unavailable: {exception.Message}. A coordinator-issued certificate may be required; provide --native-certificate-path.", exception);
    }
}

async Task RunP2P(BenchmarkOptions options)
{
    if (!options.Insecure) throw new InvalidOperationException("The local P2P benchmark is intentionally unauthenticated; pass --insecure explicitly.");
    try
    {
        using GnsRuntime runtime = GnsRuntime.Initialize(new GnsRuntimeOptions { NativeLibraryPath = options.NativePath, RequireNativeAuthentication = false });
        using GnsServer server = GnsServer.ListenP2P(0, Math.Max(64, options.Iterations));
        server.SecurityPolicy = new TransportSecurityPolicy { RequireAuthenticated = false, RequireEncrypted = false };
        SteamNetworkingIdentity identity = default;
        if (!ISteamNetworkingSockets.User!.GetIdentity(out identity) || identity.IsInvalid())
            throw new InvalidOperationException("The native GNS backend did not provide a usable local P2P identity.");

        using GnsClient client = GnsClient.ConnectP2P(identity.ToString());
        client.SecurityPolicy = new TransportSecurityPolicy { RequireAuthenticated = false, RequireEncrypted = false };
        var connected = new ManualResetEventSlim();
        client.Connected += connected.Set;
        Stopwatch connectTimer = Stopwatch.StartNew();
        if (!connected.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Timed out waiting for the local P2P connection.");
        connectTimer.Stop();

        byte[] payload = new byte[Math.Clamp(options.PayloadBytes, 1, 64 * 1024)];
        int echoed = 0;
        Stopwatch timer = Stopwatch.StartNew();
        for (int iteration = 0; iteration < options.Iterations; iteration++)
        {
            client.Send(payload, ESteamNetworkingSendType.UnreliableNoDelay);
            Stopwatch wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromMilliseconds(250))
            {
                foreach (ReceivedMessage message in server.Poll()) server.Send(message.Connection, message.Data, ESteamNetworkingSendType.UnreliableNoDelay);
                if (client.Poll().Count > 0) { echoed++; break; }
                await Task.Yield();
            }
        }
        timer.Stop();
        benchmarkResults.Add(new { name = "local P2P identity/loopback", operations = echoed, bytes = echoed * payload.Length * 2L, elapsedMs = timer.Elapsed.TotalMilliseconds, connectMs = connectTimer.Elapsed.TotalMilliseconds, identity = identity.ToString() });
        Console.WriteLine($"GNS local P2P loopback: {echoed:N0}/{options.Iterations:N0} echoed | connect={connectTimer.Elapsed.TotalMilliseconds:N1} ms | elapsed={timer.Elapsed.TotalMilliseconds:N1} ms");
    }
    catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException or TimeoutException)
    {
        benchmarkResults.Add(new { name = "local P2P identity/loopback", available = false, error = exception.Message });
        Console.WriteLine($"GNS local P2P loopback: unavailable ({exception.Message})");
        Console.WriteLine("Provide a native library and the rendezvous/signaling environment required by GNS P2P, then pass --native-path <path>.");
    }
}

static void RunSaturation(BenchmarkOptions options)
{
    var points = new List<SaturationPoint>();
    byte[]? certificate = options.NativeCertificatePath is null ? null : File.ReadAllBytes(options.NativeCertificatePath);
    using GnsRuntime runtime = GnsRuntime.Initialize(new GnsRuntimeOptions { NativeLibraryPath = options.NativePath ?? "/opt/gns/lib/libGameNetworkingSockets.so", NativeCertificate = certificate, RequireNativeAuthentication = false, DebugOutput = (level, message) => Console.Error.WriteLine($"[GNS {level}] {message}") });
    foreach ((int burst, int port) in new[] { (1, 27991), (2, 27992), (4, 27993), (8, 27994), (16, 27995), (32, 27996) })
    {
        TransportResult result = BenchmarkTransport(options with { Address = WithPort(options.Address, port), Iterations = Math.Min(options.Iterations, 500), NativePath = options.NativePath ?? "/opt/gns/lib/libGameNetworkingSockets.so" }, burst, port, sharedRuntime: runtime);
        long offered = (long)options.Clients * burst * Math.Max(1, options.PayloadBytes);
        points.Add(new(burst, result.Messages, result.Messages / result.Elapsed.TotalSeconds, result.Loss, result.P50.TotalMilliseconds, result.P99.TotalMilliseconds, offered));
        Console.WriteLine($"Saturation burst={burst}: {result.Messages:N0} echoed | {result.Messages / result.Elapsed.TotalSeconds:N0} msg/s | offered={offered / 1024d:N1} KiB/tick | RTT p50={result.P50.TotalMilliseconds:N2} ms p99={result.P99.TotalMilliseconds:N2} ms | loss={result.Loss:P2}");
    }
    if (options.JsonPath is not null)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { generatedAt = DateTimeOffset.UtcNow, scenario = "saturation", clients = options.Clients, payloadBytes = options.PayloadBytes, points }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(options.JsonPath, json);
        string graphPath = Path.ChangeExtension(options.JsonPath, ".html");
        File.WriteAllText(graphPath, SaturationGraph(points));
        Console.WriteLine($"Saturation JSON: {options.JsonPath}"); Console.WriteLine($"Saturation graph: {graphPath}");
    }
}

static string WithPort(string address, int port)
{
    if (address.StartsWith("[", StringComparison.Ordinal))
    {
        int end = address.IndexOf(']');
        if (end < 0) throw new ArgumentException("IPv6 address must include a closing bracket.", nameof(address));
        return address[..(end + 1)] + ":" + port;
    }
    int separator = address.LastIndexOf(':');
    return separator < 0 ? $"{address}:{port}" : address[..separator] + ":" + port;
}

static string SaturationGraph(IReadOnlyList<SaturationPoint> points)
{
    string rows = string.Join("", points.Select(p => $"<tr><td>{p.Burst}</td><td>{p.Echoed}</td><td>{p.MessagesPerSecond:0}</td><td>{p.LossPercent:P2}</td><td>{p.P50Milliseconds:0.00}</td><td>{p.P99Milliseconds:0.00}</td></tr>"));
    string bars = string.Join("", points.Select((p, i) => $"<rect x=\"{50 + i * 90}\" y=\"{260 - Math.Min(220, p.MessagesPerSecond / Math.Max(1, points.Max(x => x.MessagesPerSecond)) * 220):0}\" width=\"55\" height=\"220\" fill=\"#3b82f6\"><title>burst {p.Burst}: {p.MessagesPerSecond:0} msg/s, loss {p.LossPercent:P2}</title></rect>"));
    return $"<!doctype html><meta charset=utf-8><title>GNS.NET saturation</title><h1>GNS.NET saturation ramp</h1><svg viewBox=\"0 0 {Math.Max(160, points.Count * 90 + 50)} 300\" width=\"100%\" height=300><line x1=20 y1=260 x2=100% y2=260 stroke=black />{bars}</svg><table border=1><tr><th>Burst</th><th>Echoed</th><th>Msg/s</th><th>Loss</th><th>p50 ms</th><th>p99 ms</th></tr>{rows}</table>";
}

static async Task RunPlayFab(BenchmarkOptions options)
{
    (string titleId, string secretKey, _, string? testUsername, string? testPassword, string? testEmail) = PlayFabEnvironment.Load();
    if (string.IsNullOrWhiteSpace(testUsername) || string.IsNullOrWhiteSpace(testPassword)) throw new InvalidOperationException("PLAYFAB_TEST_USERNAME and PLAYFAB_TEST_PASSWORD must be configured for the persistent PlayFab benchmark account.");
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var playFab = new PlayFabRestClient(titleId, http);
    Stopwatch timer = Stopwatch.StartNew();
    PlayFabAccountSession account = options.RegisterTestAccount
        ? await playFab.RegisterAsync(testEmail ?? $"{testUsername}@example.com", testPassword, testUsername)
        : await playFab.LoginAsync(testUsername, testPassword);
    PlayFabEntitySession player = await playFab.EnsureEntitySessionAsync(account);
    PlayFabEntitySession server = await playFab.RegisterServerAsync($"gns-benchmark-server-{Guid.NewGuid():N}", secretKey);
    PlayFabLobby lobby = await playFab.CreateServerLobbyAsync(server, new PlayFabLobbyOptions { MaxPlayers = Math.Max(2, options.Clients), UseConnections = true });
    if (string.IsNullOrWhiteSpace(lobby.ConnectionString)) throw new InvalidDataException("PlayFab server lobby did not return a connection string.");
    PlayFabLobby joined = await playFab.JoinLobbyAsync(player, lobby.ConnectionString);
    timer.Stop();
    Console.WriteLine($"PlayFab end-to-end: registered account={account.PlayFabId}, server entity={server.Entity.Id}, lobby={joined.LobbyId}, elapsed={timer.Elapsed.TotalSeconds:N2}s");
}

static TransportResult BenchmarkTransport(BenchmarkOptions options, int burst, int port = 27991, bool authenticated = false, GnsRuntime? sharedRuntime = null)
{
    if (!authenticated && !options.Insecure) throw new InvalidOperationException("Native authentication is not configured for this loopback harness. Pass --insecure explicitly for development-only transport coverage.");
    byte[]? certificate = options.NativeCertificatePath is null ? null : File.ReadAllBytes(options.NativeCertificatePath);
    using GnsRuntime? ownedRuntime = sharedRuntime is null ? GnsRuntime.Initialize(new GnsRuntimeOptions { NativeLibraryPath = options.NativePath, NativeCertificate = certificate, RequireNativeAuthentication = authenticated, Impairment = options.NativeLossPercent == 0 ? null : new GnsImpairmentOptions { LossSendPercent = options.NativeLossPercent, LossReceivePercent = options.NativeLossPercent }, DebugOutput = (level, message) => Console.Error.WriteLine($"[GNS {level}] {message}") }) : null;
    using GnsServer server = GnsServer.Listen($"[::]:{port}", Math.Max(64, options.Clients * 2));
    server.SecurityPolicy = new TransportSecurityPolicy { RequireAuthenticated = authenticated, RequireEncrypted = authenticated };
    var clients = new List<GnsClient>(options.Clients);
    var connected = new CountdownEvent(options.Clients);
    var connectTimes = new List<TimeSpan>();
    object sync = new();
    server.ClientConnected += _ => { };
    try
    {
        for (int i = 0; i < options.Clients; i++)
        {
            Stopwatch started = Stopwatch.StartNew();
            GnsClient client = GnsClient.Connect(options.Address, Math.Max(64, options.Iterations));
            client.SecurityPolicy = new TransportSecurityPolicy { RequireAuthenticated = authenticated, RequireEncrypted = authenticated };
            client.Connected += () => { lock (sync) connectTimes.Add(started.Elapsed); connected.Signal(); };
            client.Disconnected += (reason, debug) => Console.Error.WriteLine($"[GNS client disconnect] {reason}: {debug}");
            clients.Add(client);
        }
        Stopwatch connectWait = Stopwatch.StartNew();
        while (!connected.IsSet && connectWait.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(1);
        if (!connected.IsSet) throw new TimeoutException("Timed out waiting for GNS loopback connections.");

        byte[] payload = new byte[Math.Max(8, Math.Clamp(options.PayloadBytes, 1, 64 * 1024))];
        var sequences = new SequenceLossTracker(checked(options.Clients * options.Iterations * Math.Max(1, burst) + 1));
        long received = 0;
        List<double> rtts = new();
        Stopwatch timer = Stopwatch.StartNew();
        for (int tick = 0; tick < options.Iterations; tick++)
        {
            long sentAt = Stopwatch.GetTimestamp();
            foreach (GnsClient client in clients)
                for (int message = 0; message < burst; message++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(payload, sequences.Next());
                    client.Send(payload, ESteamNetworkingSendType.UnreliableNoDelay);
                }
            long iterationReceived = 0; int expected = checked(clients.Count * burst); Stopwatch pump = Stopwatch.StartNew();
            while (iterationReceived < expected && pump.Elapsed < TimeSpan.FromMilliseconds(100))
            {
                foreach (ReceivedMessage message in server.Poll()) server.Send(message.Connection, message.Data, ESteamNetworkingSendType.UnreliableNoDelay);
                foreach (GnsClient client in clients)
                    foreach (ReceivedMessage message in client.Poll())
                    {
                        iterationReceived++;
                        if (message.Data.Length >= 4) sequences.Observe(BinaryPrimitives.ReadUInt32LittleEndian(message.Data));
                    }
                if (iterationReceived < expected) Thread.Yield();
            }
            received += iterationReceived;
            if (iterationReceived > 0) rtts.Add((Stopwatch.GetTimestamp() - sentAt) * 1000d / Stopwatch.Frequency);
        }
        timer.Stop();
        double loss = sequences.LossPercent / 100d;
        rtts.Sort();
        TimeSpan Percentile(double p) => rtts.Count == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(rtts[(int)Math.Clamp(Math.Ceiling(rtts.Count * p) - 1, 0, rtts.Count - 1)]);
        connectTimes.Sort();
        TimeSpan ConnectPercentile(double p) => TimeSpan.FromMilliseconds(connectTimes.Count == 0 ? 0 : connectTimes[(int)Math.Clamp(Math.Ceiling(connectTimes.Count * p) - 1, 0, connectTimes.Count - 1)].TotalMilliseconds);
        return new(received, received * (long)payload.Length * 2, timer.Elapsed, loss, Percentile(.50), Percentile(.99), ConnectPercentile(.50), sequences.Sent, sequences.ReceivedUnique, sequences.Duplicates, sequences.Missing);
    }
    finally { foreach (GnsClient client in clients) client.Dispose(); }
}

static BenchmarkResult BenchmarkSerialization(BenchmarkOptions options)
{
    BenchmarkState state = new(42, 12.5f, -8.25f, 100, "benchmark-player", new byte[options.PayloadBytes]);
    byte[] warmup = NetSerializer.Serialize(state);
    for (int i = 0; i < 1_000; i++) _ = NetSerializer.Deserialize<BenchmarkState>(warmup);
    long before = GC.GetAllocatedBytesForCurrentThread();
    Stopwatch timer = Stopwatch.StartNew();
    long bytes = 0;
    for (int i = 0; i < options.Iterations; i++) { byte[] encoded = NetSerializer.Serialize(state); _ = NetSerializer.Deserialize<BenchmarkState>(encoded); bytes += encoded.Length * 2L; }
    timer.Stop();
    return new(options.Iterations, bytes, timer.Elapsed, GC.GetAllocatedBytesForCurrentThread() - before);
}

static BenchmarkResult BenchmarkBatch(BenchmarkOptions options)
{
    NetBatch batch = CreateBatch(options.Entities);
    byte[] encoded = batch.Encode();
    for (int i = 0; i < 100; i++) _ = NetBatch.Decode(encoded);
    long before = GC.GetAllocatedBytesForCurrentThread();
    Stopwatch timer = Stopwatch.StartNew();
    for (int i = 0; i < options.Iterations; i++) _ = NetBatch.Decode(batch.Encode());
    timer.Stop();
    return new(options.Iterations, (long)encoded.Length * options.Iterations * 2, timer.Elapsed, GC.GetAllocatedBytesForCurrentThread() - before);
}

static BenchmarkResult BenchmarkConcurrentSerialization(BenchmarkOptions options)
{
    BenchmarkState state = new(42, 12.5f, -8.25f, 100, "benchmark-player", new byte[options.PayloadBytes]);
    byte[] warmup = NetSerializer.Serialize(state);
    for (int i = 0; i < 1_000; i++) _ = NetSerializer.Deserialize<BenchmarkState>(warmup);
    long before = GC.GetTotalAllocatedBytes(true), bytes = 0;
    Stopwatch timer = Stopwatch.StartNew();
    Parallel.For(0, options.Clients, new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism }, clientIndex =>
    {
        long localBytes = 0;
        for (int i = 0; i < options.Iterations; i++) { byte[] encoded = NetSerializer.Serialize(state); _ = NetSerializer.Deserialize<BenchmarkState>(encoded); localBytes += encoded.Length * 2L; }
        Interlocked.Add(ref bytes, localBytes);
    });
    timer.Stop();
    return new(options.Clients * (long)options.Iterations, bytes, timer.Elapsed, GC.GetTotalAllocatedBytes(true) - before);
}

static BenchmarkResult BenchmarkPipeline(BenchmarkOptions options)
{
    List<BenchmarkEntity> entities = Enumerable.Range(0, options.Entities).Select(i => new BenchmarkEntity(i, i % 100, i % 70)).ToList();
    var interest = new InterestManager<int, BenchmarkEntity>();
    var delta = new DeltaCompressor<BenchmarkSnapshot>((_, current) => current, (_, current) => current);
    var pipeline = new SnapshotPipeline<int, BenchmarkEntity, BenchmarkSnapshot>(interest, delta, e => (e.X, e.Y));
    for (int client = 0; client < options.Clients; client++) interest.SetView(client, new(50, 35, 35));
    long before = GC.GetAllocatedBytesForCurrentThread();
    Stopwatch timer = Stopwatch.StartNew();
    long operations = 0, bytes = 0;
    for (int tick = 1; tick <= options.Iterations; tick++) for (int client = 0; client < options.Clients; client++)
    {
        pipeline.Queue(client, entities, new BenchmarkSnapshot((uint)tick, client, entities.Count), 1, 2, (uint)tick, 1f);
        IReadOnlyList<(NetFrame Frame, NetChannel Channel)> frames = pipeline.Drain(client, 4_096);
        operations += frames.Count; bytes += frames.Sum(f => f.Frame.Payload.Length);
        pipeline.Acknowledge(client, new BenchmarkSnapshot((uint)tick, client, entities.Count));
    }
    timer.Stop();
    return new(operations, bytes, timer.Elapsed, GC.GetAllocatedBytesForCurrentThread() - before);
}

static BenchmarkResult BenchmarkPrediction(BenchmarkOptions options)
{
    long before = GC.GetAllocatedBytesForCurrentThread();
    Stopwatch timer = Stopwatch.StartNew();
    long operations = 0;
    for (int client = 0; client < options.Clients; client++)
    {
        var prediction = new ClientPrediction<BenchmarkInput, BenchmarkState>();
        for (uint tick = 1; tick <= options.Iterations; tick++) prediction.Add(tick, new BenchmarkInput(1, 0));
        _ = prediction.Reconcile((uint)(options.Iterations / 2), new BenchmarkState(), (state, input) => state with { X = state.X + input.Dx });
        operations += options.Iterations;
    }
    timer.Stop();
    return new(operations, 0, timer.Elapsed, GC.GetAllocatedBytesForCurrentThread() - before);
}

static BenchmarkResult BenchmarkReplay(BenchmarkOptions options)
{
    var recorder = new NetworkRecorder();
    for (int i = 0; i < options.Iterations; i++) recorder.Record(true, BitConverter.GetBytes(i), DateTimeOffset.UnixEpoch.AddTicks(i));
    string path = Path.Combine(Path.GetTempPath(), $"gnsnet-ci-{Guid.NewGuid():N}.gnsr"); Stopwatch timer = Stopwatch.StartNew();
    try
    {
        recorder.SaveAsync(path).GetAwaiter().GetResult(); NetworkRecorder loaded = NetworkRecorder.LoadAsync(path).GetAwaiter().GetResult();
        int delivered = loaded.ReplayTransportAsync(_ => ValueTask.FromResult(true), speed: double.MaxValue).GetAwaiter().GetResult(); timer.Stop();
        if (delivered != options.Iterations) throw new InvalidDataException("Replay did not deliver every captured packet.");
        string fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(loaded.Packets.SelectMany(x => BitConverter.GetBytes(x.Time.UtcTicks).Concat(x.Data)).ToArray()));
        return new(delivered, loaded.Packets.Sum(x => (long)x.Data.Length), timer.Elapsed, GC.GetAllocatedBytesForCurrentThread(), fingerprint);
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

static NetBatch CreateBatch(int count)
{
    var batch = new NetBatch();
    for (uint i = 0; i < count; i++) batch.Add(new NetFrame(1, i, [1, 2, 3, 4, 5, 6, 7, 8]));
    return batch;
}

[MemoryPackable] public partial record BenchmarkState(int Tick = 0, float X = 0, float Y = 0, int Health = 100, string Name = "", byte[]? Payload = null);
[MemoryPackable] public partial record BenchmarkEntity(int Id, float X, float Y);
[MemoryPackable] public partial record BenchmarkSnapshot(uint Tick, int Client, int EntityCount);
[MemoryPackable] public partial record BenchmarkInput(float Dx, float Dy);
readonly record struct BenchmarkResult(long Operations, long Bytes, TimeSpan Elapsed, long Allocated, string? Fingerprint = null);
readonly record struct MatrixProfile(int Clients, int Entities, int PayloadBytes, double LossPercent, bool Reconnect);
readonly record struct MatrixProfileResult(MatrixProfile Profile, int Frames, int CapturedPackets, int DeliveredPackets, int ReplayedLifecycleRecords);
readonly record struct NativeMatrixProfile(int Clients, int PayloadBytes, int LossPercent, bool Reconnect);
readonly record struct NativeMatrixProfileResult(NativeMatrixProfile Profile, long SentSequences, long UniqueSequences, long MissingSequences, long DuplicateSequences, long Bytes);

sealed record BenchmarkOptions(string Scenario, int Clients, int Entities, int Iterations, int PayloadBytes, int Parallelism, string? NativePath, string Address, string? NativeCertificatePath, int NativeLossPercent, bool RegisterTestAccount, bool Insecure, string? JsonPath, bool Deterministic)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        string scenario = Value(args, "--scenario") ?? "all";
        int clients = Number(args, "--clients", 16), entities = Number(args, "--entities", 1_000), iterations = Number(args, "--iterations", 10_000), payload = Number(args, "--payload-bytes", 128);
        int parallelism = Number(args, "--parallelism", Environment.ProcessorCount);
        if (scenario is not ("all" or "serialize" or "stress" or "batch" or "pipeline" or "prediction" or "replay" or "transport" or "authenticated-transport" or "p2p" or "native-matrix" or "saturation" or "playfab" or "matrix")) throw new ArgumentException("--scenario must be all, serialize, stress, batch, pipeline, prediction, replay, transport, authenticated-transport, p2p, native-matrix, saturation, playfab, or matrix.");
        if (clients < 1 || entities < 1 || iterations < 1 || payload < 0 || parallelism < 1) throw new ArgumentException("Benchmark sizes must be positive; payload may be zero.");
        int nativeLoss = Number(args, "--native-loss-percent", 0);
        if (nativeLoss is < 0 or > 100) throw new ArgumentException("--native-loss-percent must be between 0 and 100.");
        return new(scenario, clients, entities, iterations, payload, parallelism, Value(args, "--native-path"), Value(args, "--address") ?? "127.0.0.1:27991", Value(args, "--native-certificate-path"), nativeLoss, args.Contains("--register-test-account", StringComparer.OrdinalIgnoreCase), args.Contains("--insecure", StringComparer.OrdinalIgnoreCase), Value(args, "--json"), args.Contains("--deterministic", StringComparer.OrdinalIgnoreCase));
    }
    private static int Number(string[] args, string name, int fallback) => int.TryParse(Value(args, name), out int value) ? value : fallback;
    private static string? Value(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
}

readonly record struct TransportResult(long Messages, long Bytes, TimeSpan Elapsed, double Loss, TimeSpan P50, TimeSpan P99, TimeSpan ConnectP50, long SentSequences = 0, long UniqueSequences = 0, long DuplicateSequences = 0, long MissingSequences = 0);
readonly record struct SaturationPoint(int Burst, long Echoed, double MessagesPerSecond, double LossPercent, double P50Milliseconds, double P99Milliseconds, long OfferedBytesPerTick);
