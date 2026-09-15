namespace GnsNet.Stride;

using global::Stride.Core;
using global::Stride.Games;

/// <summary>Creates and installs the adapter's central game system.</summary>
public static class NetworkBootstrap
{
    /// <summary>Creates a system wired to the supplied queue and registries.</summary>
    public static NetworkGameSystem Create(
        IServiceRegistry services,
        NetworkCommandQueue? commands = null,
        NetworkPrefabRegistry? prefabs = null,
        NetworkClock? clock = null)
        => new(services, commands, prefabs, clock);

    /// <summary>Installs a system in Stride's update collection.</summary>
    public static NetworkGameSystem Install(
        IServiceRegistry services,
        IGameSystemCollection systems,
        NetworkCommandQueue? commands = null,
        NetworkPrefabRegistry? prefabs = null,
        NetworkClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(systems);
        NetworkGameSystem system = Create(services, commands, prefabs, clock);
        systems.Add(system);
        return system;
    }
}
