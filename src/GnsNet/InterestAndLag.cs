namespace GnsNet;

public readonly record struct InterestPoint(float X, float Y, float Radius);

/// <summary>Filters entities per client before snapshot encoding.</summary>
public sealed class InterestManager<TClientId, TEntity> where TClientId : notnull
{
    private readonly Dictionary<TClientId, InterestPoint> views = new();
    private readonly Dictionary<TClientId, Func<TEntity, bool>> visibility = new();
    public void SetView(TClientId client, InterestPoint view) => this.views[client] = view;
    public void SetVisibility(TClientId client, Func<TEntity, bool> predicate) => this.visibility[client] = predicate ?? throw new ArgumentNullException(nameof(predicate));
    public IEnumerable<TEntity> Cull(TClientId client, IEnumerable<TEntity> entities, Func<TEntity, (float X, float Y)> position)
    {
        if (!this.views.TryGetValue(client, out InterestPoint view)) return [];
        float radiusSquared = view.Radius * view.Radius;
        Func<TEntity, bool>? visible = this.visibility.TryGetValue(client, out Func<TEntity, bool>? predicate) ? predicate : null;
        return entities.Where(entity =>
        {
            if (visible is not null && !visible(entity)) return false;
            var p = position(entity); float dx = p.X - view.X, dy = p.Y - view.Y;
            return dx * dx + dy * dy <= radiusSquared;
        });
    }
    public void Remove(TClientId client) { this.views.Remove(client); this.visibility.Remove(client); }
}

/// <summary>Stores recent authoritative states for server-side lag-compensated queries.</summary>
public sealed class LagCompensationHistory<TState>
{
    private readonly TimeSpan window;
    private readonly LinkedList<(DateTimeOffset Time, TState State)> history = new();
    public LagCompensationHistory(TimeSpan window) { if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window)); this.window = window; }
    public void Record(TState state, DateTimeOffset? now = null)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.UtcNow;
        this.history.AddLast((timestamp, state));
        while (this.history.First is not null && timestamp - this.history.First.Value.Time > this.window) this.history.RemoveFirst();
    }
    public bool TryGet(DateTimeOffset target, out TState state)
    {
        if (this.history.First is null || target < this.history.First.Value.Time) { state = default!; return false; }
        var best = this.history.Last;
        while (best?.Previous is not null && best.Previous.Value.Time >= target) best = best.Previous;
        if (best is null) { state = default!; return false; }
        state = best.Value.State; return true;
    }
    public bool TryGet(DateTimeOffset target, Func<TState, TState, double, TState> interpolate, out TState state)
    {
        ArgumentNullException.ThrowIfNull(interpolate);
        if (this.history.First is null || target < this.history.First.Value.Time) { state = default!; return false; }
        var next = this.history.First;
        while (next?.Next is not null && next.Value.Time < target) next = next.Next;
        if (next is null) { state = default!; return false; }
        if (next.Value.Time == target || next.Previous is null) { state = next.Value.State; return true; }
        var previous = next.Previous.Value; double span = (next.Value.Time - previous.Time).TotalSeconds;
        state = interpolate(previous.State, next.Value.State, span <= 0 ? 1 : Math.Clamp((target - previous.Time).TotalSeconds / span, 0, 1)); return true;
    }
    public bool TryExecuteAt<TResult>(DateTimeOffset target, Func<TState, TResult> query, out TResult result)
    {
        if (!this.TryGet(target, out TState state)) { result = default!; return false; }
        result = query(state); return true;
    }
    public bool TryExecuteAt<TResult>(DateTimeOffset target, Func<TState, TState, double, TState> interpolate, Func<TState, TResult> query, out TResult result)
    {
        if (!this.TryGet(target, interpolate, out TState state)) { result = default!; return false; }
        result = query(state); return true;
    }
}
