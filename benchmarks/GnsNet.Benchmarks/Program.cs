using System.Diagnostics;
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
if (options.Scenario is "saturation") RunSaturation(options);
if (options.Scenario is "playfab") await RunPlayFab(options);
if (options.Scenario is "all" or "batch") Run("NetBatch encode + decode", options, BenchmarkBatch);
if (options.Scenario is "all" or "pipeline") Run("AOI + delta + priority snapshot pipeline", options, BenchmarkPipeline);
if (options.Scenario is "all" or "prediction") Run("client prediction reconciliation", options, BenchmarkPrediction);
if (options.Scenario is "all" or "replay") Run("persisted capture replay", options, BenchmarkReplay);
if (options.JsonPath is not null)
{
    File.WriteAllText(options.JsonPath, System.Text.Json.JsonSerializer.Serialize(new { generatedAt = DateTimeOffset.UtcNow, scenario = options.Scenario, clients = options.Clients, entities = options.Entities, iterations = options.Iterations, results = benchmarkResults }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Benchmark JSON: {options.JsonPath}");
}

void Run(string name, BenchmarkOptions options, Func<BenchmarkOptions, BenchmarkResult> benchmark)
{
    BenchmarkResult result = benchmark(options);
    benchmarkResults.Add(new { name, result.Operations, result.Bytes, elapsedMs = result.Elapsed.TotalMilliseconds, result.Allocated, opsPerSecond = result.Operations / Math.Max(result.Elapsed.TotalSeconds, double.Epsilon) });
    Console.WriteLine($"{name}: {result.Operations:N0} ops | {result.Operations / result.Elapsed.TotalSeconds:N0} ops/s | {result.Bytes / 1024d / 1024d:N2} MiB processed | {result.Allocated / (double)Math.Max(1, result.Operations):N1} B/op | {result.Elapsed.TotalMilliseconds:N1} ms");
}

static void RunTransport(BenchmarkOptions options)
{
    try
    {
        TransportResult result = BenchmarkTransport(options, 1);
        Console.WriteLine($"GNS loopback transport: {result.Messages:N0} echoed | {result.Messages / result.Elapsed.TotalSeconds:N0} messages/s | {result.Bytes / 1024d / 1024d:N2} MiB on wire | RTT p50={result.P50.TotalMilliseconds:N2} ms p99={result.P99.TotalMilliseconds:N2} ms | loss={result.Loss:P2} | connect p50={result.ConnectP50.TotalMilliseconds:N2} ms");
    }
    catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
    {
        Console.WriteLine($"GNS loopback transport: unavailable ({exception.Message})");
        Console.WriteLine("Build or provide the native GameNetworkingSockets library, then pass --native-path <path>.");
    }
}

static void RunSaturation(BenchmarkOptions options)
{
    var points = new List<SaturationPoint>();
    foreach (int burst in new[] { 1, 2, 4, 8, 16, 32 })
    {
        TransportResult result = BenchmarkTransport(options with { Iterations = Math.Min(options.Iterations, 500), NativePath = options.NativePath ?? "/opt/gns/lib/libGameNetworkingSockets.so" }, burst);
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

static TransportResult BenchmarkTransport(BenchmarkOptions options, int burst)
{
    using GnsRuntime runtime = GnsRuntime.Initialize(new GnsRuntimeOptions { NativeLibraryPath = options.NativePath, RequireNativeAuthentication = false, DebugOutput = (level, message) => Console.Error.WriteLine($"[GNS {level}] {message}") });
    using GnsServer server = GnsServer.Listen("[::]:27991", Math.Max(64, options.Clients * 2));
    server.SecurityPolicy = new TransportSecurityPolicy { RequireAuthenticated = false, RequireEncrypted = false };
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
            GnsClient client = GnsClient.Connect("127.0.0.1:27991", Math.Max(64, options.Iterations));
            client.SecurityPolicy = new TransportSecurityPolicy { RequireAuthenticated = false, RequireEncrypted = false };
            client.Connected += () => { lock (sync) connectTimes.Add(started.Elapsed); connected.Signal(); };
            client.Disconnected += (reason, debug) => Console.Error.WriteLine($"[GNS client disconnect] {reason}: {debug}");
            clients.Add(client);
        }
        Stopwatch connectWait = Stopwatch.StartNew();
        while (!connected.IsSet && connectWait.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(1);
        if (!connected.IsSet) throw new TimeoutException("Timed out waiting for GNS loopback connections.");

        byte[] payload = new byte[Math.Clamp(options.PayloadBytes, 1, 64 * 1024)];
        long sent = 0, received = 0;
        List<double> rtts = new();
        Stopwatch timer = Stopwatch.StartNew();
        for (int tick = 0; tick < options.Iterations; tick++)
        {
            long sentAt = Stopwatch.GetTimestamp();
            foreach (GnsClient client in clients) for (int message = 0; message < burst; message++) { client.Send(payload, ESteamNetworkingSendType.UnreliableNoDelay); sent++; }
            long iterationReceived = 0; int expected = checked(clients.Count * burst); Stopwatch pump = Stopwatch.StartNew();
            while (iterationReceived < expected && pump.Elapsed < TimeSpan.FromMilliseconds(100))
            {
                foreach (ReceivedMessage message in server.Poll()) server.Send(message.Connection, message.Data, ESteamNetworkingSendType.UnreliableNoDelay);
                foreach (GnsClient client in clients) iterationReceived += client.Poll().Count;
                if (iterationReceived < expected) Thread.Yield();
            }
            received += iterationReceived;
            if (iterationReceived > 0) rtts.Add((Stopwatch.GetTimestamp() - sentAt) * 1000d / Stopwatch.Frequency);
        }
        timer.Stop();
        double loss = sent == 0 ? 0 : 1d - (double)received / sent;
        rtts.Sort();
        TimeSpan Percentile(double p) => rtts.Count == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(rtts[(int)Math.Clamp(Math.Ceiling(rtts.Count * p) - 1, 0, rtts.Count - 1)]);
        connectTimes.Sort();
        TimeSpan ConnectPercentile(double p) => TimeSpan.FromMilliseconds(connectTimes.Count == 0 ? 0 : connectTimes[(int)Math.Clamp(Math.Ceiling(connectTimes.Count * p) - 1, 0, connectTimes.Count - 1)].TotalMilliseconds);
        return new(received, received * (long)payload.Length * 2, timer.Elapsed, loss, Percentile(.50), Percentile(.99), ConnectPercentile(.50));
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
        return new(delivered, loaded.Packets.Sum(x => (long)x.Data.Length), timer.Elapsed, GC.GetAllocatedBytesForCurrentThread());
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
readonly record struct BenchmarkResult(long Operations, long Bytes, TimeSpan Elapsed, long Allocated);

sealed record BenchmarkOptions(string Scenario, int Clients, int Entities, int Iterations, int PayloadBytes, int Parallelism, string? NativePath, bool RegisterTestAccount, string? JsonPath)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        string scenario = Value(args, "--scenario") ?? "all";
        int clients = Number(args, "--clients", 16), entities = Number(args, "--entities", 1_000), iterations = Number(args, "--iterations", 10_000), payload = Number(args, "--payload-bytes", 128);
        int parallelism = Number(args, "--parallelism", Environment.ProcessorCount);
        if (scenario is not ("all" or "serialize" or "stress" or "batch" or "pipeline" or "prediction" or "replay" or "transport" or "saturation" or "playfab")) throw new ArgumentException("--scenario must be all, serialize, stress, batch, pipeline, prediction, replay, transport, saturation, or playfab.");
        if (clients < 1 || entities < 1 || iterations < 1 || payload < 0 || parallelism < 1) throw new ArgumentException("Benchmark sizes must be positive; payload may be zero.");
        return new(scenario, clients, entities, iterations, payload, parallelism, Value(args, "--native-path"), args.Contains("--register-test-account", StringComparer.OrdinalIgnoreCase), Value(args, "--json"));
    }
    private static int Number(string[] args, string name, int fallback) => int.TryParse(Value(args, name), out int value) ? value : fallback;
    private static string? Value(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
}

readonly record struct TransportResult(long Messages, long Bytes, TimeSpan Elapsed, double Loss, TimeSpan P50, TimeSpan P99, TimeSpan ConnectP50);
readonly record struct SaturationPoint(int Burst, long Echoed, double MessagesPerSecond, double LossPercent, double P50Milliseconds, double P99Milliseconds, long OfferedBytesPerTick);
