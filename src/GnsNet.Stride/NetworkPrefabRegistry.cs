namespace GnsNet.Stride;

using global::Stride.Engine;

/// <summary>Resolves replication archetypes to presentation entities without owning network state.</summary>
public sealed class NetworkPrefabRegistry
{
    private readonly Dictionary<ushort, Func<Entity>> factories = new();
    private readonly Dictionary<ushort, string> contentUrls = new();

    /// <summary>Registers a deterministic, headless-testable entity factory.</summary>
    public void RegisterFactory(ushort archetypeId, Func<Entity> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (archetypeId == 0) throw new ArgumentOutOfRangeException(nameof(archetypeId));
        if (!this.factories.TryAdd(archetypeId, factory)) throw new InvalidOperationException($"Archetype {archetypeId} is already registered.");
    }

    /// <summary>Registers a content URL for a game bootstrapper that supplies loading.</summary>
    public void RegisterContentUrl(ushort archetypeId, string contentUrl)
    {
        if (archetypeId == 0) throw new ArgumentOutOfRangeException(nameof(archetypeId));
        if (string.IsNullOrWhiteSpace(contentUrl)) throw new ArgumentException("A content URL is required.", nameof(contentUrl));
        if (!this.contentUrls.TryAdd(archetypeId, contentUrl)) throw new InvalidOperationException($"Archetype {archetypeId} is already registered.");
    }

    /// <summary>Returns a factory-created entity, or false with a diagnostic-safe reason for a missing archetype.</summary>
    public bool TryCreate(ushort archetypeId, out Entity entity, out string? missingReason)
    {
        if (this.factories.TryGetValue(archetypeId, out Func<Entity>? factory))
        {
            entity = factory() ?? throw new InvalidOperationException($"Factory for archetype {archetypeId} returned null.");
            missingReason = null;
            return true;
        }

        entity = null!;
        missingReason = this.contentUrls.TryGetValue(archetypeId, out string? url)
            ? $"Archetype {archetypeId} requires content loading for '{url}'. Supply a factory on the Stride update thread."
            : $"No presentation prefab is registered for archetype {archetypeId}.";
        return false;
    }
}
