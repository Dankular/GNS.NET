namespace GnsNet;

using System.Text;

public readonly record struct ServerLoad(int Connections, double TickMilliseconds, double CpuPercent, int PendingMessages);

/// <summary>Decides when to reject joins or reduce simulation frequency under load.</summary>
public sealed class LoadSheddingPolicy
{
    public int MaximumConnections { get; init; } = 1000;
    public int MaximumPendingMessages { get; init; } = 100_000;
    public double MaximumTickMilliseconds { get; init; } = 250;
    public double RejectCpuPercent { get; init; } = 95;
    public double ReduceTickCpuPercent { get; init; } = 80;
    public bool AllowConnection(ServerLoad load) => load.Connections < this.MaximumConnections && load.PendingMessages <= this.MaximumPendingMessages && load.TickMilliseconds <= this.MaximumTickMilliseconds && load.CpuPercent < this.RejectCpuPercent;
    public TimeSpan TickInterval(TimeSpan normal, ServerLoad load) => load.CpuPercent >= this.ReduceTickCpuPercent ? normal + normal : normal;
}

public sealed class AdaptiveTickController
{
    private readonly TimeSpan normal;
    private bool reduced;
    public AdaptiveTickController(TimeSpan normal) => this.normal = normal;
    public bool Reduced => this.reduced;
    public TimeSpan Update(ServerLoad load, LoadSheddingPolicy policy)
    {
        if (!this.reduced && load.CpuPercent >= policy.ReduceTickCpuPercent) this.reduced = true;
        else if (this.reduced && load.CpuPercent < policy.ReduceTickCpuPercent - 10) this.reduced = false;
        return this.reduced ? this.normal + this.normal : this.normal;
    }
}

/// <summary>Maps players/rooms to independently hosted match or zone processes.</summary>
public sealed class ShardDirectory<TShardId> where TShardId : notnull
{
    private readonly List<TShardId> shards = new();
    private readonly object sync = new();
    public IReadOnlyList<TShardId> Shards { get { lock (this.sync) return this.shards.ToArray(); } }
    public void Add(TShardId shard) { lock (this.sync) if (!this.shards.Contains(shard)) this.shards.Add(shard); }
    public bool Remove(TShardId shard) { lock (this.sync) return this.shards.Remove(shard); }
    public TShardId Select(string routingKey)
    {
        lock (this.sync)
        {
            if (this.shards.Count == 0) throw new InvalidOperationException("No shards are available.");
            uint hash = 2166136261;
            foreach (byte value in Encoding.UTF8.GetBytes(routingKey)) { hash ^= value; hash *= 16777619; }
            int index = (int)(hash % (uint)this.shards.Count);
            return this.shards[index];
        }
    }
}

public readonly record struct BackendMessage(string Topic, byte[] Payload, DateTimeOffset CreatedAt);

/// <summary>Process-boundary contract for matchmaker, persistence, and server-to-server traffic.</summary>
public interface IBackendMessageBus
{
    ValueTask PublishAsync(BackendMessage message, CancellationToken cancellationToken = default);
    IAsyncEnumerable<BackendMessage> SubscribeAsync(string topic, CancellationToken cancellationToken = default);
}

public sealed class InMemoryBackendMessageBus : IBackendMessageBus
{
    private readonly System.Threading.Channels.Channel<BackendMessage> channel = System.Threading.Channels.Channel.CreateUnbounded<BackendMessage>();
    public ValueTask PublishAsync(BackendMessage message, CancellationToken cancellationToken = default) => this.channel.Writer.WriteAsync(message, cancellationToken);
    public async IAsyncEnumerable<BackendMessage> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (BackendMessage message in this.channel.Reader.ReadAllAsync(cancellationToken)) if (message.Topic == topic) yield return message;
    }
}
