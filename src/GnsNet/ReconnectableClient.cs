namespace GnsNet;

using GnsSharp;

public sealed class ReconnectableClient : IAsyncDisposable
{
    private readonly Func<GnsClient> connect;
    private readonly TimeSpan initialDelay;
    private readonly TimeSpan maxDelay;
    private readonly CancellationTokenSource cts = new();
    private GnsClient? client;
    private Task? loop;
    public ReconnectableClient(Func<GnsClient> connect, TimeSpan? initialDelay = null, TimeSpan? maxDelay = null)
    {
        this.connect = connect ?? throw new ArgumentNullException(nameof(connect));
        this.initialDelay = initialDelay ?? TimeSpan.FromMilliseconds(250);
        this.maxDelay = maxDelay ?? TimeSpan.FromSeconds(10);
    }
    public GnsClient? Client => Volatile.Read(ref this.client);
    public bool ReconnectEnabled => !this.cts.IsCancellationRequested;
    public event Action<GnsClient>? Connected;
    public event Action<ESteamNetConnectionEnd, string?>? Disconnected;
    public void Start() => this.loop ??= Task.Run(() => this.RunAsync(this.cts.Token));
    public IReadOnlyList<ReceivedMessage> Poll() => this.client?.Poll() ?? [];
    public bool TrySend(ReadOnlySpan<byte> data, ESteamNetworkingSendType sendType) => Volatile.Read(ref this.client) is GnsClient current && current.Send(data, sendType) == EResult.OK;
    public void DisconnectGracefully()
    {
        this.cts.Cancel();
        if (Volatile.Read(ref this.client) is not GnsClient current) return;
        current.Send(new NetFrame(GnsServerHost<string>.GracefulDisconnectOpcode, 0, []).Encode(), ESteamNetworkingSendType.Reliable);
        current.Close();
    }
    public async ValueTask DisposeAsync()
    {
        this.cts.Cancel();
        if (this.loop is not null) try { await this.loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        Volatile.Read(ref this.client)?.Dispose();
        this.cts.Dispose();
    }
    private async Task RunAsync(CancellationToken token)
    {
        TimeSpan delay = this.initialDelay;
        while (!token.IsCancellationRequested)
        {
            GnsClient? next = null;
            try
            {
                next = this.connect();
                var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                next.Connected += () => { connected.TrySetResult(); this.Connected?.Invoke(next); };
                next.Disconnected += (reason, detail) => { disconnected.TrySetResult(); this.Disconnected?.Invoke(reason, detail); };
                Volatile.Write(ref this.client, next);
                await connected.Task.WaitAsync(token).ConfigureAwait(false);
                delay = this.initialDelay;
                await Task.WhenAny(Task.Delay(Timeout.InfiniteTimeSpan, token), disconnected.Task).ConfigureAwait(false);
                if (disconnected.Task.IsCompleted) throw new IOException("GNS connection closed.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch { }
            finally { if (ReferenceEquals(Volatile.Read(ref this.client), next)) Volatile.Write(ref this.client, null); next?.Dispose(); }
            if (!token.IsCancellationRequested) { await Task.Delay(delay, token).ConfigureAwait(false); delay = TimeSpan.FromMilliseconds(Math.Min(this.maxDelay.TotalMilliseconds, delay.TotalMilliseconds * 2)); }
        }
    }
}
