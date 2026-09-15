namespace GnsNet.Stride;

using global::Stride.Engine;

/// <summary>Adapter diagnostics supplied by the host without exposing player identifiers as metric labels.</summary>
public readonly record struct NetworkDiagnosticsSnapshot(
    string SessionState,
    NetworkClockSnapshot Clock,
    int ActiveViews,
    int KnownEntities,
    int QueueDepth,
    long DroppedState,
    long Corrections,
    long ResimulatedTicks);

/// <summary>Opt-in text overlay hook. A game can route the generated text to its own UI system.</summary>
public sealed class NetworkDebugOverlay : SyncScript
{
    /// <summary>Enables overlay output.</summary>
    public bool Enabled { get; set; }
    /// <summary>Supplies current diagnostics.</summary>
    public Func<NetworkDiagnosticsSnapshot>? SnapshotProvider { get; set; }
    /// <summary>Receives formatted output, typically a game-owned UI text property.</summary>
    public Action<string>? TextSink { get; set; }

    /// <summary>Formats a stable, human-readable diagnostics panel.</summary>
    public static string Format(in NetworkDiagnosticsSnapshot value)
        => $"net {value.SessionState} views={value.ActiveViews}/{value.KnownEntities} queue={value.QueueDepth} " +
           $"rtt={value.Clock.RoundTrip.TotalMilliseconds:F1}ms jitter={value.Clock.Jitter.TotalMilliseconds:F1}ms " +
           $"offset={value.Clock.OffsetMilliseconds:F2}ms drift={value.Clock.DriftPartsPerMillion:F1}ppm " +
           $"ticks server={value.Clock.ServerTick} render={value.Clock.RenderTick:F2} delay={value.Clock.InterpolationDelayTicks:F2} " +
           $"drop={value.DroppedState} corrections={value.Corrections} resim={value.ResimulatedTicks}";

    /// <inheritdoc />
    public override void Update() { if (this.Enabled && this.SnapshotProvider is not null) this.TextSink?.Invoke(Format(this.SnapshotProvider())); }
}
