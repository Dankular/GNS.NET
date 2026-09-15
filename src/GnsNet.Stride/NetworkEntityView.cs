namespace GnsNet.Stride;

using global::Stride.Engine;

/// <summary>Presentation marker attached to a Stride entity owned by a network view.</summary>
public sealed class NetworkEntityView : StartupScript
{
    /// <summary>Stable server-assigned network entity ID.</summary>
    public ulong NetworkId { get; internal set; }

    /// <summary>Replication archetype used to resolve the presentation prefab.</summary>
    public ushort ArchetypeId { get; internal set; }

    /// <summary>Whether this view is the local predicted player.</summary>
    public bool IsLocalPlayer { get; internal set; }

    /// <summary>Last authoritative tick applied to this view.</summary>
    public uint LastTick { get; internal set; }
}
