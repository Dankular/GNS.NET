namespace GnsNet;

/// <summary>Tracks observer enter/leave transitions from an AOI query.</summary>
public sealed class ObserverTracker<TClientId, TEntity> where TClientId : notnull where TEntity : notnull
{
    private readonly Dictionary<TClientId, HashSet<TEntity>> observers = new();
    public event Action<TClientId, TEntity>? Entered;
    public event Action<TClientId, TEntity>? Left;
    public IReadOnlyCollection<TEntity> Current(TClientId client) => this.observers.TryGetValue(client, out HashSet<TEntity>? set) ? set.ToArray() : [];
    public void Update(TClientId client, IEnumerable<TEntity> visible)
    {
        HashSet<TEntity> next = visible.ToHashSet(); if (!this.observers.TryGetValue(client, out HashSet<TEntity>? previous)) this.observers[client] = previous = new();
        foreach (TEntity entity in next.Except(previous).ToArray()) { previous.Add(entity); this.Entered?.Invoke(client, entity); }
        foreach (TEntity entity in previous.Except(next).ToArray()) { previous.Remove(entity); this.Left?.Invoke(client, entity); }
    }
    public void Remove(TClientId client) => this.observers.Remove(client);

    /// <summary>Removes an entity from every observer set without emitting a leave transition.</summary>
    public void Forget(TEntity entity)
    {
        foreach (HashSet<TEntity> set in this.observers.Values) set.Remove(entity);
    }
}
