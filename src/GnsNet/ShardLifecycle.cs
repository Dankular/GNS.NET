namespace GnsNet;

public readonly record struct ShardLease<TId>(TId Shard, DateTimeOffset ExpiresAt, bool Draining);

/// <summary>Tracks shard ownership, health leases, draining, and player migration.</summary>
public sealed class ShardLifecycle<TShardId, TPlayerId> where TShardId : notnull where TPlayerId : notnull
{
    private readonly TimeSpan leaseDuration;
    private readonly Dictionary<TShardId, ShardLease<TShardId>> leases = new();
    private readonly Dictionary<TPlayerId, TShardId> players = new();
    public ShardLifecycle(TimeSpan leaseDuration) => this.leaseDuration = leaseDuration > TimeSpan.Zero ? leaseDuration : throw new ArgumentOutOfRangeException(nameof(leaseDuration));
    public bool Register(TShardId shard, DateTimeOffset? now = null) { this.leases[shard] = new(shard, (now ?? DateTimeOffset.UtcNow) + this.leaseDuration, false); return true; }
    public bool Renew(TShardId shard, DateTimeOffset? now = null) { if (!this.leases.ContainsKey(shard)) return false; this.leases[shard] = new(shard, (now ?? DateTimeOffset.UtcNow) + this.leaseDuration, this.leases[shard].Draining); return true; }
    public bool SetDraining(TShardId shard, bool draining) { if (!this.leases.ContainsKey(shard)) return false; this.leases[shard] = this.leases[shard] with { Draining = draining }; return true; }
    public bool Assign(TPlayerId player, TShardId shard) { if (!this.leases.TryGetValue(shard, out var lease) || lease.Draining || lease.ExpiresAt <= DateTimeOffset.UtcNow) return false; this.players[player] = shard; return true; }
    public bool Migrate(TPlayerId player, TShardId target) { if (!this.leases.TryGetValue(target, out var lease) || lease.Draining || lease.ExpiresAt <= DateTimeOffset.UtcNow) return false; this.players[player] = target; return true; }
    public bool IsAvailable(TShardId shard) => this.leases.TryGetValue(shard, out var lease) && !lease.Draining && lease.ExpiresAt > DateTimeOffset.UtcNow;
    public bool TryGetPlayerShard(TPlayerId player, out TShardId shard) => this.players.TryGetValue(player, out shard!);
    public IReadOnlyList<TShardId> Shards => this.leases.Keys.ToArray();
    public void Expire(DateTimeOffset? now = null) { var t = now ?? DateTimeOffset.UtcNow; foreach (var p in this.leases.Where(x => x.Value.ExpiresAt <= t).ToArray()) { this.leases.Remove(p.Key); foreach (var player in this.players.Where(x => EqualityComparer<TShardId>.Default.Equals(x.Value, p.Key)).Select(x => x.Key).ToArray()) this.players.Remove(player); } }
}
