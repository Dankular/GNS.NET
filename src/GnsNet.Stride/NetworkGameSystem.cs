namespace GnsNet.Stride;

using global::Stride.Core;
using global::Stride.Games;

/// <summary>Central Stride update-thread adapter for immutable GNS.NET presentation commands.</summary>
public sealed class NetworkGameSystem : GameSystemBase
{
    private readonly NetworkCommandQueue commands;
    private readonly NetworkPrefabRegistry prefabs;
    private readonly NetworkTransformProcessor transforms = new();
    private readonly NetworkAnimationProcessor animations = new();
    private bool destroyed;

    /// <summary>Clock used for server/prediction/render timeline sampling.</summary>
    public NetworkClock Clock { get; }

    /// <summary>One-to-one network entity view registry.</summary>
    public NetworkEntityViewRegistry Views { get; }

    /// <summary>Creates an adapter system; no transport is owned or started here.</summary>
    public NetworkGameSystem(IServiceRegistry services, NetworkCommandQueue? commands = null, NetworkPrefabRegistry? prefabs = null, NetworkClock? clock = null)
        : base(services)
    {
        this.commands = commands ?? new NetworkCommandQueue();
        this.prefabs = prefabs ?? new NetworkPrefabRegistry();
        this.Clock = clock ?? new NetworkClock();
        this.Views = new NetworkEntityViewRegistry();
    }

    /// <summary>Enqueues a validated decoded command from a transport/network callback.</summary>
    public bool Enqueue(NetworkCommand command) => !this.destroyed && this.commands.Enqueue(command);

    /// <inheritdoc />
    public override void Update(GameTime gameTime)
    {
        if (this.destroyed) return;
        this.Views.BindUpdateThread();
        Span<NetworkCommand> batch = stackalloc NetworkCommand[256];
        int count = this.commands.Drain(batch);
        this.Clock.Update(count == 0 ? this.Clock.Snapshot.ServerTick : batch[count - 1].Tick);
        for (int i = 0; i < count; i++) if (batch[i].IsLifecycle) this.ApplyLifecycle(batch[i]);
        for (int i = 0; i < count; i++) if (!batch[i].IsLifecycle) this.ApplyState(batch[i]);
        foreach (NetworkEntityView view in this.Views.Snapshot())
            if (!view.IsLocalPlayer) this.transforms.ApplyRemote(view, this.Clock.Snapshot.RenderTick);
    }

    /// <inheritdoc />
    protected override void Destroy()
    {
        if (this.destroyed) return;
        this.Views.BindUpdateThread();
        this.Views.Clear();
        this.commands.Clear();
        this.Views.Dispose();
        this.destroyed = true;
        base.Destroy();
    }

    private void ApplyLifecycle(NetworkCommand command)
    {
        if (command.Kind == NetworkCommandKind.Despawn)
        {
            if (this.Views.TryGet(command.NetworkId, out _)) this.Views.Despawn(command.NetworkId);
            this.transforms.Remove(command.NetworkId);
            this.animations.Remove(command.NetworkId);
            return;
        }

        if (!this.prefabs.TryCreate(command.ArchetypeId, out global::Stride.Engine.Entity entity, out _)) return;
        this.Views.TrySpawn(command.NetworkId, command.ArchetypeId, command.IsLocalPlayer, () => entity, out _);
    }

    private void ApplyState(NetworkCommand command)
    {
        if (!this.Views.TryGet(command.NetworkId, out NetworkEntityView? view)) return;
        if (command.Kind == NetworkCommandKind.Transform)
        {
            this.transforms.AddSnapshot(view, command.Tick, command.Transform);
            view.LastTick = command.Tick;
        }
        else if (command.Kind == NetworkCommandKind.Animation) this.animations.Apply(view, command.Animation);
    }

}
