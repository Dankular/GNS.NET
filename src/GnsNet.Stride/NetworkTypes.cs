namespace GnsNet.Stride;

using System.Numerics;

/// <summary>Presentation-only transform data decoded from an engine-independent network contract.</summary>
public readonly record struct NetworkTransformState(Vector3 Position, Quaternion Rotation, Vector3 Velocity)
{
    /// <summary>Creates a transform with an identity rotation and no velocity.</summary>
    public NetworkTransformState(Vector3 position) : this(position, Quaternion.Identity, Vector3.Zero) { }
}

/// <summary>Compact gameplay animation state. Bone transforms are intentionally not replicated.</summary>
public readonly record struct NetworkAnimationState(
    ushort LocomotionId,
    float NormalizedSpeed,
    bool Grounded,
    byte Stance,
    ushort AbilityId,
    uint ActionSequence,
    uint ServerStartTick);

/// <summary>Lifecycle and component command kinds consumed by the Stride update thread.</summary>
public enum NetworkCommandKind : byte
{
    Spawn,
    Despawn,
    Transform,
    Animation,
}

/// <summary>An immutable, bounded presentation command. It contains no transport-owned buffers.</summary>
public readonly record struct NetworkCommand(
    NetworkCommandKind Kind,
    ulong NetworkId,
    ushort ArchetypeId,
    uint Tick,
    NetworkTransformState Transform,
    NetworkAnimationState Animation,
    bool IsLocalPlayer)
{
    /// <summary>Creates a spawn command.</summary>
    public static NetworkCommand Spawn(ulong id, ushort archetypeId, bool local = false)
        => new(NetworkCommandKind.Spawn, id, archetypeId, 0, default, default, local);

    /// <summary>Creates an idempotent despawn command.</summary>
    public static NetworkCommand Despawn(ulong id)
        => new(NetworkCommandKind.Despawn, id, 0, 0, default, default, false);

    /// <summary>Creates a remote or local transform snapshot.</summary>
    public static NetworkCommand TransformSnapshot(ulong id, uint tick, NetworkTransformState state, bool local = false)
        => new(NetworkCommandKind.Transform, id, 0, tick, state, default, local);

    /// <summary>Creates a compact animation update.</summary>
    public static NetworkCommand AnimationSnapshot(ulong id, uint tick, NetworkAnimationState state)
        => new(NetworkCommandKind.Animation, id, 0, tick, default, state, false);

    /// <summary>Gets whether this command is lifecycle/control data that must not be shed before state.</summary>
    public bool IsLifecycle => Kind is NetworkCommandKind.Spawn or NetworkCommandKind.Despawn;
}
