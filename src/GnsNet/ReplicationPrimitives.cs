namespace GnsNet;

using System.IO.Compression;
using System.Collections.Concurrent;

/// <summary>Bounded compression codec for snapshot payloads.</summary>
public static class SnapshotCompression
{
    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var stream = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true)) stream.Write(data);
        return output.ToArray();
    }
    public static byte[] Decompress(ReadOnlySpan<byte> data, int maximumBytes = 8 * 1024 * 1024)
    {
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        using var input = new MemoryStream(data.ToArray()); using var stream = new BrotliStream(input, CompressionMode.Decompress); using var output = new MemoryStream();
        byte[] buffer = new byte[81920]; int read; while ((read = stream.Read(buffer)) > 0) { if (output.Length + read > maximumBytes) throw new InvalidDataException("Decompressed snapshot exceeds the configured limit."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
}

/// <summary>Thread-safe bounded encoded snapshot cache keyed by an immutable state/version key.</summary>
public sealed class SnapshotEncodeCache<TKey> where TKey : notnull
{
    private readonly int capacity;
    private readonly ConcurrentDictionary<TKey, Lazy<byte[]>> entries = new();
    private readonly ConcurrentQueue<TKey> order = new();
    public SnapshotEncodeCache(int capacity = 1024) { if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity)); this.capacity = capacity; }
    public int Count => this.entries.Count;
    public byte[] GetOrAdd(TKey key, Func<byte[]> encode)
    {
        ArgumentNullException.ThrowIfNull(encode);
        if (this.entries.TryGetValue(key, out Lazy<byte[]>? existing)) return existing.Value;
        Lazy<byte[]> lazy = this.entries.GetOrAdd(key, _ => new Lazy<byte[]>(encode, LazyThreadSafetyMode.ExecutionAndPublication)); this.order.Enqueue(key);
        while (this.entries.Count > this.capacity && this.order.TryDequeue(out TKey? old)) this.entries.TryRemove(old, out _);
        return lazy.Value;
    }
    public bool Remove(TKey key) => this.entries.TryRemove(key, out _);
    public void Clear() { this.entries.Clear(); while (this.order.TryDequeue(out _)) { } }
}

/// <summary>Encodes independent snapshot records concurrently while sharing a bounded cache.</summary>
public sealed class ParallelSnapshotEncoder<TItem, TKey> where TKey : notnull
{
    private readonly SnapshotEncodeCache<TKey> cache;
    public ParallelSnapshotEncoder(SnapshotEncodeCache<TKey>? cache = null) => this.cache = cache ?? new SnapshotEncodeCache<TKey>();
    public IReadOnlyList<byte[]> Encode(IReadOnlyList<TItem> items, Func<TItem, TKey> key, Func<TItem, byte[]> encode)
    {
        ArgumentNullException.ThrowIfNull(items); ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(encode);
        byte[][] result = new byte[items.Count][];
        Parallel.For(0, items.Count, i => result[i] = this.cache.GetOrAdd(key(items[i]), () => encode(items[i])));
        return result;
    }
}

/// <summary>Spatial-hash AOI that avoids scanning every entity for every connection.</summary>
public sealed class SpatialHashInterestManager<TClientId, TEntity> where TClientId : notnull
{
    private readonly float cellSize;
    private readonly Dictionary<(int X, int Y), List<TEntity>> cells = new();
    private readonly Dictionary<TClientId, InterestPoint> views = new();
    public SpatialHashInterestManager(float cellSize = 32)
    { if (!(cellSize > 0) || float.IsNaN(cellSize)) throw new ArgumentOutOfRangeException(nameof(cellSize)); this.cellSize = cellSize; }
    public void SetView(TClientId client, InterestPoint view) => this.views[client] = view;
    public void Rebuild(IEnumerable<TEntity> entities, Func<TEntity, (float X, float Y)> position)
    {
        this.cells.Clear();
        foreach (TEntity entity in entities) { var p = position(entity); var key = this.Key(p.X, p.Y); if (!this.cells.TryGetValue(key, out List<TEntity>? list)) this.cells[key] = list = new(); list.Add(entity); }
    }
    public IEnumerable<TEntity> Cull(TClientId client, Func<TEntity, (float X, float Y)> position, Func<TEntity, bool>? visibility = null)
    {
        if (!this.views.TryGetValue(client, out InterestPoint view)) return [];
        int minX = (int)MathF.Floor((view.X - view.Radius) / this.cellSize), maxX = (int)MathF.Floor((view.X + view.Radius) / this.cellSize), minY = (int)MathF.Floor((view.Y - view.Radius) / this.cellSize), maxY = (int)MathF.Floor((view.Y + view.Radius) / this.cellSize);
        float radiusSquared = view.Radius * view.Radius;
        return Enumerate();
        IEnumerable<TEntity> Enumerate()
        {
            for (int x = minX; x <= maxX; x++) for (int y = minY; y <= maxY; y++) if (this.cells.TryGetValue((x, y), out List<TEntity>? list)) foreach (TEntity entity in list)
            { var p = position(entity); float dx = p.X - view.X, dy = p.Y - view.Y; if (dx * dx + dy * dy <= radiusSquared && (visibility is null || visibility(entity))) yield return entity; }
        }
    }
    public IEnumerable<TEntity> Cull(TClientId client, Func<TEntity, (float X, float Y)> position, Func<TEntity, string?> scene, Func<TEntity, string?> team, string? clientScene, string? clientTeam, Func<TEntity, bool>? visibility = null)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(team);
        return this.Cull(client, position, entity => (clientScene is null || scene(entity) == clientScene) && (clientTeam is null || team(entity) == clientTeam) && (visibility is null || visibility(entity)));
    }
    public IEnumerable<TEntity> Cull(TClientId client, Func<TEntity, (float X, float Y)> position, Func<TEntity, string?> scene, Func<TEntity, string?> team, Func<TEntity, string?> owner, string? clientScene, string? clientTeam, string? clientOwner, Func<TEntity, bool>? visibility = null, Func<TEntity, bool>? occlusion = null)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(team); ArgumentNullException.ThrowIfNull(owner);
        return this.Cull(client, position, entity => (clientScene is null || scene(entity) == clientScene) && (clientTeam is null || team(entity) == clientTeam) && (clientOwner is null || owner(entity) == clientOwner) && (visibility is null || visibility(entity)) && (occlusion is null || occlusion(entity)));
    }
    private (int X, int Y) Key(float x, float y) => ((int)MathF.Floor(x / this.cellSize), (int)MathF.Floor(y / this.cellSize));
}

public readonly record struct RewindHitbox(long EntityId, float X, float Y, float Radius);
public readonly record struct RewindHit(long EntityId, float Distance, uint Tick);

/// <summary>Authorizes rewind queries against a measured view time and a server-defined maximum.</summary>
public sealed class RewindAuthorization
{
    public RewindAuthorization(TimeSpan maximumRewind) { if (maximumRewind <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(maximumRewind)); MaximumRewind = maximumRewind; }
    public TimeSpan MaximumRewind { get; }
    public bool TryGetAuthorizedTime(DateTimeOffset serverNow, DateTimeOffset claimedViewTime, out DateTimeOffset authorized)
    { DateTimeOffset floor = serverNow - MaximumRewind; authorized = claimedViewTime < floor ? floor : claimedViewTime > serverNow ? serverNow : claimedViewTime; return claimedViewTime >= floor && claimedViewTime <= serverNow; }
}

/// <summary>Tracks measured client view times so rewind callers cannot supply an arbitrary timestamp.</summary>
public sealed class RewindViewTimeRegistry<TClientId> where TClientId : notnull
{
    private readonly Dictionary<TClientId, DateTimeOffset> viewTimes = new();
    public void Record(TClientId client, DateTimeOffset measuredViewTime) => this.viewTimes[client] = measuredViewTime;
    public bool TryGetAuthorized(TClientId client, DateTimeOffset serverNow, RewindAuthorization authorization, out DateTimeOffset authorized)
    {
        ArgumentNullException.ThrowIfNull(authorization); authorized = default;
        return this.viewTimes.TryGetValue(client, out DateTimeOffset claimed) && authorization.TryGetAuthorizedTime(serverNow, claimed, out authorized);
    }
    public bool Remove(TClientId client) => this.viewTimes.Remove(client);
}

/// <summary>Stores bounded historical hitboxes and performs authoritative 2D ray-circle rewind queries.</summary>
public sealed class HitboxRewindHistory
{
    private readonly int capacity;
    private readonly int maxHitboxesPerFrame;
    private readonly LinkedList<(uint Tick, RewindHitbox[] Hitboxes)> frames = new();
    public HitboxRewindHistory(int capacity = 128, int maxHitboxesPerFrame = 4096) { if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity)); if (maxHitboxesPerFrame < 1) throw new ArgumentOutOfRangeException(nameof(maxHitboxesPerFrame)); this.capacity = capacity; this.maxHitboxesPerFrame = maxHitboxesPerFrame; }
    public int Count => this.frames.Count;
    public long RejectedHitboxes { get; private set; }
    public IReadOnlyList<RewindHit> RaycastAuthorized(DateTimeOffset serverNow, DateTimeOffset claimedViewTime, RewindAuthorization authorization, Func<DateTimeOffset, uint> tickForTime, float originX, float originY, float directionX, float directionY, float maxDistance)
    {
        ArgumentNullException.ThrowIfNull(authorization); ArgumentNullException.ThrowIfNull(tickForTime);
        return authorization.TryGetAuthorizedTime(serverNow, claimedViewTime, out DateTimeOffset authorized) ? this.Raycast(tickForTime(authorized), originX, originY, directionX, directionY, maxDistance) : [];
    }
    public IReadOnlyList<RewindHit> RaycastForClient<TClientId>(TClientId client, DateTimeOffset serverNow, RewindViewTimeRegistry<TClientId> viewTimes, RewindAuthorization authorization, Func<DateTimeOffset, uint> tickForTime, float originX, float originY, float directionX, float directionY, float maxDistance) where TClientId : notnull
    {
        ArgumentNullException.ThrowIfNull(viewTimes); ArgumentNullException.ThrowIfNull(tickForTime);
        return viewTimes.TryGetAuthorized(client, serverNow, authorization, out DateTimeOffset authorized) ? this.Raycast(tickForTime(authorized), originX, originY, directionX, directionY, maxDistance) : [];
    }
    public void Record(uint tick, IEnumerable<RewindHitbox> hitboxes)
    {
        RewindHitbox[] all = hitboxes?.ToArray() ?? throw new ArgumentNullException(nameof(hitboxes));
        if (all.Length > this.maxHitboxesPerFrame) { this.RejectedHitboxes += all.Length - this.maxHitboxesPerFrame; all = all[..this.maxHitboxesPerFrame]; }
        this.frames.AddLast((tick, all)); while (this.frames.Count > this.capacity) this.frames.RemoveFirst();
    }
    public IReadOnlyList<RewindHit> Raycast(uint tick, float originX, float originY, float directionX, float directionY, float maxDistance)
    {
        if (maxDistance < 0 || directionX == 0 && directionY == 0) return [];
        (uint Tick, RewindHitbox[] Hitboxes) frame = this.frames.OrderBy(x => Math.Abs((long)x.Tick - tick)).FirstOrDefault();
        if (frame.Hitboxes is null) return [];
        float length = MathF.Sqrt(directionX * directionX + directionY * directionY); directionX /= length; directionY /= length;
        var hits = new List<RewindHit>();
        foreach (RewindHitbox box in frame.Hitboxes)
        {
            float ox = originX - box.X, oy = originY - box.Y, projection = -(ox * directionX + oy * directionY); float closestX = ox + directionX * projection, closestY = oy + directionY * projection;
            float distanceSquared = closestX * closestX + closestY * closestY; if (distanceSquared <= box.Radius * box.Radius && projection <= maxDistance)
            { float offset = MathF.Sqrt(MathF.Max(0, box.Radius * box.Radius - distanceSquared)); hits.Add(new(box.EntityId, MathF.Max(0, projection - offset), frame.Tick)); }
        }
        return hits.OrderBy(x => x.Distance).ToArray();
    }
    /// <summary>Performs a rewind ray query at a fractional tick, interpolating between retained frames.</summary>
    public IReadOnlyList<RewindHit> RaycastSubTick(double tick, float originX, float originY, float directionX, float directionY, float maxDistance)
    {
        if (double.IsNaN(tick) || tick < 0 || tick > uint.MaxValue) return [];
        uint lower = (uint)Math.Floor(tick); uint upper = (uint)Math.Ceiling(tick);
        (uint Tick, RewindHitbox[] Hitboxes) first = this.frames.OrderBy(x => Math.Abs((long)x.Tick - lower)).FirstOrDefault();
        (uint Tick, RewindHitbox[] Hitboxes) second = this.frames.OrderBy(x => Math.Abs((long)x.Tick - upper)).FirstOrDefault();
        if (first.Hitboxes is null || second.Hitboxes is null || first.Tick == second.Tick) return this.Raycast(first.Tick, originX, originY, directionX, directionY, maxDistance);
        double amount = Math.Clamp((tick - first.Tick) / (second.Tick - first.Tick), 0, 1); var byId = second.Hitboxes.ToDictionary(x => x.EntityId);
        var blended = new List<RewindHitbox>(); foreach (RewindHitbox box in first.Hitboxes) if (byId.TryGetValue(box.EntityId, out RewindHitbox next)) blended.Add(new(box.EntityId, (float)(box.X + (next.X - box.X) * amount), (float)(box.Y + (next.Y - box.Y) * amount), (float)(box.Radius + (next.Radius - box.Radius) * amount)));
        var temporary = new HitboxRewindHistory(2); temporary.Record(0, blended); return temporary.Raycast(0, originX, originY, directionX, directionY, maxDistance).Select(x => x with { Tick = lower }).ToArray();
    }
    /// <summary>Rewinds and tests a sphere against registered hitboxes.</summary>
    public IReadOnlyList<RewindHit> SphereCast(uint tick, float x, float y, float radius)
    {
        if (radius < 0) return [];
        (uint Tick, RewindHitbox[] Hitboxes) frame = this.frames.OrderBy(v => Math.Abs((long)v.Tick - tick)).FirstOrDefault(); if (frame.Hitboxes is null) return [];
        return frame.Hitboxes.Where(box => MathF.Pow(box.X - x, 2) + MathF.Pow(box.Y - y, 2) <= MathF.Pow(box.Radius + radius, 2)).Select(box => new RewindHit(box.EntityId, MathF.Max(0, MathF.Sqrt(MathF.Pow(box.X - x, 2) + MathF.Pow(box.Y - y, 2)) - box.Radius), frame.Tick)).OrderBy(x => x.Distance).ToArray();
    }
    /// <summary>Rewinds and tests an axis-aligned box against registered hitboxes.</summary>
    public IReadOnlyList<RewindHit> BoxCast(uint tick, float minX, float minY, float maxX, float maxY)
    {
        if (maxX < minX || maxY < minY) return [];
        (uint Tick, RewindHitbox[] Hitboxes) frame = this.frames.OrderBy(v => Math.Abs((long)v.Tick - tick)).FirstOrDefault(); if (frame.Hitboxes is null) return [];
        return frame.Hitboxes.Where(box => Math.Clamp(box.X, minX, maxX) - box.X is var dx && Math.Clamp(box.Y, minY, maxY) - box.Y is var dy && dx * dx + dy * dy <= box.Radius * box.Radius).Select(box => new RewindHit(box.EntityId, 0, frame.Tick)).ToArray();
    }
}
