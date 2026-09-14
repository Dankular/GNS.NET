namespace GnsNet;

using System.Diagnostics;
using System.Diagnostics.Metrics;

/// <summary>OpenTelemetry-compatible instrumentation for transport and replication hosts.</summary>
public static class GnsTelemetry
{
    public const string ActivitySourceName = "GnsNet";
    public const string MeterName = "GnsNet";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);
    public static readonly Counter<long> BytesIn = Meter.CreateCounter<long>("gnsnet.bytes.in", unit: "By");
    public static readonly Counter<long> BytesOut = Meter.CreateCounter<long>("gnsnet.bytes.out", unit: "By");
    public static readonly Counter<long> PacketsDropped = Meter.CreateCounter<long>("gnsnet.packets.dropped", unit: "{packet}");
    public static readonly Histogram<double> Rtt = Meter.CreateHistogram<double>("gnsnet.rtt", unit: "ms");
    public static readonly UpDownCounter<long> PendingReliable = Meter.CreateUpDownCounter<long>("gnsnet.pending.reliable", unit: "By");
    public static Activity? Start(string operation, string? connectionId = null) => ActivitySource.StartActivity(operation, ActivityKind.Internal, default(ActivityContext), new ActivityTagsCollection { { "gnsnet.connection.id", connectionId } });
    public static void RecordInbound(long bytes, string? connectionId = null) => BytesIn.Add(bytes, new KeyValuePair<string, object?>("gnsnet.connection.id", connectionId));
    public static void RecordOutbound(long bytes, string? connectionId = null) => BytesOut.Add(bytes, new KeyValuePair<string, object?>("gnsnet.connection.id", connectionId));
    public static void RecordDrop(string? connectionId = null) => PacketsDropped.Add(1, new KeyValuePair<string, object?>("gnsnet.connection.id", connectionId));
    public static void RecordRtt(double milliseconds, string? connectionId = null) => Rtt.Record(milliseconds, new KeyValuePair<string, object?>("gnsnet.connection.id", connectionId));
}
