namespace GnsNet;

using GnsSharp;

/// <summary>
/// Owns the process-wide GameNetworkingSockets lifecycle: loading the native library,
/// <c>GameNetworkingSockets.Init</c>/<c>Kill</c>, and pumping <c>RunCallbacks()</c> on a
/// background loop. Only one instance should exist per process - GNS itself is a global,
/// not a per-instance resource.
/// </summary>
/// <remarks>
/// This wrapper only supports the open-source GNS backend (as opposed to the Steamworks SDK
/// backend GnsSharp can also target), matching the backend the original POC was built against.
/// </remarks>
public sealed class GnsRuntime : IDisposable, IAsyncDisposable
{
    private readonly nint nativeLibrary;
    private readonly CancellationTokenSource cts = new();
    private readonly Task callbackPump;

    // Kept alive for the lifetime of the runtime: GNS calls back into this via a native
    // function pointer, so it must not be garbage collected while callbacks might fire.
    private readonly FSteamNetworkingSocketsDebugOutput? debugOutput;

    private bool disposed;

    private GnsRuntime(nint nativeLibrary, GnsRuntimeOptions options)
    {
        this.nativeLibrary = nativeLibrary;
        this.debugOutput = options.DebugOutput;
        this.callbackPump = Task.Run(() => PumpCallbacksAsync(options.CallbackInterval, this.cts.Token));
    }

    /// <summary>
    /// Loads the native library, initializes GameNetworkingSockets, and starts the callback pump.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The referenced GnsSharp package was built for the Steamworks SDK backend rather than the
    /// open-source GNS backend this wrapper requires.
    /// </exception>
    /// <exception cref="InvalidOperationException"><c>GameNetworkingSockets_Init</c> failed.</exception>
    public static GnsRuntime Initialize(GnsRuntimeOptions? options = null)
    {
        options ??= new GnsRuntimeOptions();

        if (GnsSharpCore.Backend != GnsSharpCore.BackendKind.OpenSource)
        {
            throw new NotSupportedException(
                $"GnsNet requires the open-source GameNetworkingSockets backend, but the referenced " +
                $"GnsSharp package was built for backend '{GnsSharpCore.Backend}'. Reference one of the " +
                "GnsSharp.Gns.* packages (not GnsSharp.Steamworks.*).");
        }

        nint nativeLibrary = NativeLibraryLoader.Load(options.NativeLibraryPath);

        if (!GameNetworkingSockets.Init(out string? errMsg))
        {
            System.Runtime.InteropServices.NativeLibrary.Free(nativeLibrary);
            throw new InvalidOperationException($"GameNetworkingSockets_Init failed: {errMsg}");
        }

        if (options.RequireNativeAuthentication)
        {
            ESteamNetworkingAvailability availability = ISteamNetworkingSockets.User!.InitAuthentication();
            if (availability is ESteamNetworkingAvailability.Failed or ESteamNetworkingAvailability.CannotTry)
            {
                GameNetworkingSockets.Kill();
                System.Runtime.InteropServices.NativeLibrary.Free(nativeLibrary);
                throw new InvalidOperationException($"Native GNS authentication is unavailable: {availability}.");
            }
        }

        if (options.Impairment is not null)
        {
            GnsNativeConfiguration.Apply(options, new GnsUtilsConfigurationSink(ISteamNetworkingUtils.User!));
        }
        else if (options.P2P is not null)
        {
            GnsNativeConfiguration.Apply(options, new GnsUtilsConfigurationSink(ISteamNetworkingUtils.User!));
        }

        if (options.DebugOutput is not null)
        {
            ISteamNetworkingUtils.User!.SetDebugOutputFunction(options.DebugOutputLevel, options.DebugOutput);
        }

        return new GnsRuntime(nativeLibrary, options);
    }

    private sealed class GnsUtilsConfigurationSink : IGnsNativeConfigurationSink
    {
        private readonly ISteamNetworkingUtils utils;
        public GnsUtilsConfigurationSink(ISteamNetworkingUtils utils) => this.utils = utils;
        public void SetInt32(ESteamNetworkingConfigValue key, int value) => this.utils.SetGlobalConfigValueInt32(key, value);
        public void SetString(ESteamNetworkingConfigValue key, string value) => this.utils.SetGlobalConfigValueString(key, value);
    }

    /// <summary>
    /// Stops the callback pump, shuts down GameNetworkingSockets, and frees the native library.
    /// </summary>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        this.cts.Cancel();
        try
        {
            this.callbackPump.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        this.cts.Dispose();

        GameNetworkingSockets.Kill();
        System.Runtime.InteropServices.NativeLibrary.Free(this.nativeLibrary);
    }

    /// <inheritdoc cref="Dispose"/>
    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        this.cts.Cancel();
        try
        {
            await this.callbackPump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        this.cts.Dispose();

        GameNetworkingSockets.Kill();
        System.Runtime.InteropServices.NativeLibrary.Free(this.nativeLibrary);
    }

    private static async Task PumpCallbacksAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ISteamNetworkingSockets.User?.RunCallbacks();
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}
