namespace GnsNet;

using GnsSharp;
using System.Runtime.InteropServices;

/// <summary>Native GNS connection and congestion state exposed without losing precision.</summary>
public readonly record struct NativeConnectionStatistics(
    ESteamNetworkingConnectionState State,
    int PingMilliseconds,
    float LocalQuality,
    float RemoteQuality,
    float OutPacketsPerSecond,
    float OutBytesPerSecond,
    float InPacketsPerSecond,
    float InBytesPerSecond,
    int SendRateBytesPerSecond,
    int PendingUnreliableBytes,
    int PendingReliableBytes,
    int SentUnackedReliableBytes,
    SteamNetworkingMicroseconds QueueTime,
    string? DetailedStatus)
{
    /// <summary>Native locally observed packet loss derived from GNS's 0..1 delivery quality.</summary>
    public double LocalPacketLossPercent => QualityToLoss(this.LocalQuality);

    /// <summary>Native remotely observed packet loss derived from GNS's 0..1 delivery quality.</summary>
    public double RemotePacketLossPercent => QualityToLoss(this.RemoteQuality);

    private static double QualityToLoss(float quality)
        => float.IsFinite(quality) ? (1d - Math.Clamp(quality, 0f, 1f)) * 100d : 0d;
}

/// <summary>Per-lane native GNS queue state.</summary>
public readonly record struct NativeLaneStatistics(
    int PendingUnreliableBytes,
    int PendingReliableBytes,
    int SentUnackedReliableBytes,
    SteamNetworkingMicroseconds QueueTime);

/// <summary>Native transport controls available to a connected endpoint.</summary>
public interface INativeTransportDiagnostics
{
    NativeConnectionStatistics GetStatistics();
    IReadOnlyList<NativeLaneStatistics> GetLaneStatistics(int laneCount);
    EResult ConfigureLanes(ReadOnlySpan<int> priorities, ReadOnlySpan<ushort> weights);
}

internal static class NativeTransportDiagnostics
{
    public static NativeConnectionStatistics GetStatistics(ISteamNetworkingSockets sockets, HSteamNetConnection handle)
    {
        SteamNetConnectionRealTimeStatus_t status = default;
        _ = sockets.GetConnectionRealTimeStatus(handle, out status);
        string? detailed = null;
        _ = sockets.GetDetailedConnectionStatus(handle, out detailed, 16 * 1024);
        return new(status.State, status.Ping, status.ConnectionQualityLocal, status.ConnectionQualityRemote,
            status.OutPacketsPerSec, status.OutBytesPerSec, status.InPacketsPerSec, status.InBytesPerSec,
            status.SendRateBytesPerSecond, status.PendingUnreliable, status.PendingReliable,
            status.SentUnackedReliable, status.QueueTime, detailed);
    }

    public static IReadOnlyList<NativeLaneStatistics> GetLaneStatistics(ISteamNetworkingSockets sockets, HSteamNetConnection handle, int laneCount)
    {
        if (laneCount is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(laneCount));
        SteamNetConnectionRealTimeLaneStatus_t[] lanes = new SteamNetConnectionRealTimeLaneStatus_t[laneCount];
        _ = sockets.GetConnectionRealTimeStatus(handle, lanes);
        return lanes.Select(x => new NativeLaneStatistics(x.PendingUnreliable, x.PendingReliable, x.SentUnackedReliable, x.QueueTime)).ToArray();
    }

    public static EResult ConfigureLanes(ISteamNetworkingSockets sockets, HSteamNetConnection handle, ReadOnlySpan<int> priorities, ReadOnlySpan<ushort> weights)
    {
        if (priorities.Length == 0) throw new ArgumentException("At least one lane is required.", nameof(priorities));
        if (priorities.Length != weights.Length && weights.Length != 0) throw new ArgumentException("Lane priorities and weights must have equal lengths, or omit all weights.", nameof(weights));
        return sockets.ConfigureConnectionLanes(handle, priorities.Length, priorities, weights);
    }

    public static EResult SendOnLane(ISteamNetworkingSockets sockets, HSteamNetConnection handle, ReadOnlySpan<byte> data, ESteamNetworkingSendType sendType, ushort lane)
    {
        ISteamNetworkingUtils utils = ISteamNetworkingUtils.User ?? throw new InvalidOperationException("GNS utilities are not initialized.");
        nint messagePtr = utils.AllocateMessage(data.Length);
        if (messagePtr == 0) return EResult.InvalidState;
        try
        {
            SteamNetworkingMessage_t message = Marshal.PtrToStructure<SteamNetworkingMessage_t>(messagePtr);
            if (data.Length != 0) Marshal.Copy(data.ToArray(), 0, message.Data, data.Length);
            message.Connection = handle;
            message.Size = data.Length;
            message.Flags = sendType;
            message.IdxLane = lane;
            Marshal.StructureToPtr(message, messagePtr, false);
            Span<nint> messages = stackalloc nint[1] { messagePtr };
            Span<long> results = stackalloc long[1];
            sockets.SendMessages(messages, results);
            return (EResult)results[0];
        }
        finally
        {
            SteamNetworkingMessage_t.Release(messagePtr);
        }
    }
}
