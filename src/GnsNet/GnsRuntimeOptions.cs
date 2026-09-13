namespace GnsNet;

using GnsSharp;

/// <summary>
/// Options for <see cref="GnsRuntime.Initialize"/>.
/// </summary>
public sealed class GnsRuntimeOptions
{
    /// <summary>
    /// Explicit path to the native GameNetworkingSockets library. When null, resolved via
    /// <see cref="NativeLibraryLoader.ResolvePath"/> (env var, then platform default next to the executable).
    /// </summary>
    public string? NativeLibraryPath { get; init; }

    /// <summary>
    /// How often the background task calls <c>ISteamNetworkingSockets.RunCallbacks()</c>.
    /// GnsSharp's own examples poll at 16ms; that's the default here too.
    /// </summary>
    public TimeSpan CallbackInterval { get; init; } = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// Optional diagnostic output sink, wired up via <c>ISteamNetworkingUtils.SetDebugOutputFunction</c>.
    /// Leave null to skip registering one.
    /// </summary>
    public FSteamNetworkingSocketsDebugOutput? DebugOutput { get; init; }

    /// <summary>
    /// Detail level for <see cref="DebugOutput"/>. Ignored if <see cref="DebugOutput"/> is null.
    /// </summary>
    public ESteamNetworkingSocketsDebugOutputType DebugOutputLevel { get; init; } = ESteamNetworkingSocketsDebugOutputType.Warning;
}
