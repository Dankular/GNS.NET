namespace GnsNet.Stride;

using global::Stride.Engine;

/// <summary>Optional game-owned destination for compact replicated animation parameters.</summary>
public interface INetworkAnimationSink
{
    /// <summary>Applies the latest state to game-owned animation parameters.</summary>
    void Apply(in NetworkAnimationState state);
}

/// <summary>Script component that exposes the latest animation parameters to a game animation graph.</summary>
public class NetworkAnimationBinding : SyncScript, INetworkAnimationSink
{
    /// <summary>Last accepted animation state.</summary>
    public NetworkAnimationState State { get; private set; }

    /// <inheritdoc />
    public void Apply(in NetworkAnimationState state) => this.State = state;

    /// <inheritdoc />
    public override void Update() { }
}

/// <summary>Routes animation updates to a game-owned sink and ignores late packets.</summary>
public sealed class NetworkAnimationProcessor
{
    private readonly Dictionary<ulong, uint> lastSequences = new();

    /// <summary>Applies a state to the first binding on the view's entity.</summary>
    public bool Apply(NetworkEntityView view, NetworkAnimationState state)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (this.lastSequences.TryGetValue(view.NetworkId, out uint previous) && state.ActionSequence < previous) return false;
        this.lastSequences[view.NetworkId] = state.ActionSequence;
        INetworkAnimationSink? sink = view.Entity.Get<NetworkAnimationBinding>();
        sink?.Apply(state);
        return sink is not null;
    }

    /// <summary>Removes sequence state after despawn.</summary>
    public void Remove(ulong networkId) => this.lastSequences.Remove(networkId);
}
