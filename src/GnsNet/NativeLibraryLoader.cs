namespace GnsNet;

using GnsSharp;
using System.Runtime.InteropServices;

/// <summary>
/// Resolves and loads the native GameNetworkingSockets shared library that GnsSharp's
/// P/Invoke layer calls into. GnsSharp does not ship this binary - see the repository
/// README for how to build it from ValveSoftware/GameNetworkingSockets.
/// </summary>
public static class NativeLibraryLoader
{
    private static readonly object resolverLock = new();
    private static nint resolvedHandle;
    private static bool resolverInstalled;
    /// <summary>
    /// Environment variable checked for the native library path when no explicit path is given.
    /// </summary>
    public const string PathEnvironmentVariable = "GNSNET_NATIVE_LIBRARY_PATH";

    /// <summary>
    /// Resolves the native library path without loading it: <paramref name="explicitPath"/> if set,
    /// otherwise the <see cref="PathEnvironmentVariable"/> environment variable, otherwise the
    /// platform's default file name next to the running executable.
    /// </summary>
    public static string ResolvePath(string? explicitPath = null)
    {
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return explicitPath;
        }

        string? envPath = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        if (!string.IsNullOrEmpty(envPath))
        {
            return envPath;
        }

        return Path.Combine(AppContext.BaseDirectory, DefaultFileName());
    }

    /// <summary>
    /// Loads the native library, resolving its path via <see cref="ResolvePath"/> first.
    /// </summary>
    /// <exception cref="DllNotFoundException">
    /// The resolved path does not point at a loadable native library.
    /// </exception>
    public static nint Load(string? explicitPath = null)
    {
        string path = ResolvePath(explicitPath);

        try
        {
            nint handle = NativeLibrary.Load(path);
            lock (resolverLock)
            {
                if (resolvedHandle != 0 && resolvedHandle != handle)
                {
                    NativeLibrary.Free(handle);
                    throw new InvalidOperationException("Only one native GameNetworkingSockets library may be loaded per process.");
                }
                resolvedHandle = handle;
                if (!resolverInstalled)
                {
                    NativeLibrary.SetDllImportResolver(typeof(ISteamNetworkingSockets).Assembly, ResolveImport);
                    resolverInstalled = true;
                }
            }
            return handle;
        }
        catch (DllNotFoundException ex)
        {
            throw new DllNotFoundException(
                $"Could not load the native GameNetworkingSockets library from '{path}'. Build it " +
                "from https://github.com/ValveSoftware/GameNetworkingSockets (see BUILDING.md there), " +
                $"then either place it next to the executable, set the '{PathEnvironmentVariable}' " +
                $"environment variable, or pass an explicit path to {nameof(GnsRuntime)}.{nameof(GnsRuntime.Initialize)}.",
                ex);
        }
    }

    private static nint ResolveImport(string libraryName, System.Reflection.Assembly _, DllImportSearchPath? __)
        => libraryName.Contains("GameNetworkingSockets", StringComparison.OrdinalIgnoreCase) || libraryName.Contains("steamnetworkingsockets", StringComparison.OrdinalIgnoreCase)
            ? resolvedHandle
            : 0;

    private static string DefaultFileName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "GameNetworkingSockets.dll";
        }

        if (OperatingSystem.IsMacOS())
        {
            // GnsSharp's own README notes macOS is untested by upstream; the Posix64/32
            // backends are still what you'd reference, but you're on your own for the build.
            return "libGameNetworkingSockets.dylib";
        }

        return "libGameNetworkingSockets.so";
    }
}
