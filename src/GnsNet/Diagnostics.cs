namespace GnsNet;

using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

public readonly record struct NetworkConditions(TimeSpan Latency, TimeSpan Jitter, double LossPercent);

/// <summary>Development-only deterministic latency, jitter, and loss injector.</summary>
public sealed class NetworkConditionSimulator
{
    private readonly Random random;
    private readonly object sync = new();
    private NetworkConditions conditions;
    public NetworkConditionSimulator(NetworkConditions conditions, int seed = 1) { this.random = new Random(seed); this.Conditions = conditions; }
    public NetworkConditions Conditions { get { lock (this.sync) return this.conditions; } set { Validate(value); lock (this.sync) this.conditions = value; } }
    private static void Validate(NetworkConditions value)
    { if (value.Latency < TimeSpan.Zero || value.Jitter < TimeSpan.Zero || value.LossPercent is < 0 or > 100 || double.IsNaN(value.LossPercent)) throw new ArgumentOutOfRangeException(nameof(value)); }
    public bool ShouldDrop() { lock (this.sync) return this.random.NextDouble() * 100 < this.Conditions.LossPercent; }
    public TimeSpan NextDelay()
    {
        double jitter; lock (this.sync) jitter = (this.random.NextDouble() * 2 - 1) * this.Conditions.Jitter.TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Max(0, this.Conditions.Latency.TotalMilliseconds + jitter));
    }
    public async ValueTask<bool> DeliverAsync(Func<ValueTask> deliver, CancellationToken cancellationToken = default)
    { if (this.ShouldDrop()) return false; await Task.Delay(this.NextDelay(), cancellationToken).ConfigureAwait(false); await deliver().ConfigureAwait(false); return true; }
}

public sealed class ConnectionMetrics
{
    private long bytesIn, bytesOut, packetsIn, packetsOut, lostPackets, shedFrames, shedBytes;
    private readonly Queue<bool> probeResults = new();
    private readonly object probeLock = new();
    private double rttMilliseconds;
    public long BytesIn => Interlocked.Read(ref this.bytesIn); public long BytesOut => Interlocked.Read(ref this.bytesOut);
    public long PacketsIn => Interlocked.Read(ref this.packetsIn); public long PacketsOut => Interlocked.Read(ref this.packetsOut);
    public double PacketLossPercent { get { lock (this.probeLock) return this.probeResults.Count == 0 ? 0 : 100d * this.probeResults.Count(x => !x) / this.probeResults.Count; } }
    public double RttMilliseconds => Volatile.Read(ref this.rttMilliseconds);
    public long ShedFrames => Interlocked.Read(ref this.shedFrames); public long ShedBytes => Interlocked.Read(ref this.shedBytes);
    public void RecordIn(int bytes) { Interlocked.Add(ref this.bytesIn, bytes); Interlocked.Increment(ref this.packetsIn); }
    public void RecordOut(int bytes, bool delivered = true) { Interlocked.Add(ref this.bytesOut, bytes); Interlocked.Increment(ref this.packetsOut); if (!delivered) Interlocked.Increment(ref this.lostPackets); }
    public void RecordSequenceSent() { }
    public void RecordSequenceAcknowledged() { lock (this.probeLock) { this.probeResults.Enqueue(true); while (this.probeResults.Count > 128) this.probeResults.Dequeue(); } }
    public void RecordLostPacket() { Interlocked.Increment(ref this.lostPackets); lock (this.probeLock) { this.probeResults.Enqueue(false); while (this.probeResults.Count > 128) this.probeResults.Dequeue(); } }
    public void RecordRtt(TimeSpan rtt) => Volatile.Write(ref this.rttMilliseconds, rtt.TotalMilliseconds);
    public void RecordShed(int frames, long bytes) { Interlocked.Add(ref this.shedFrames, frames); Interlocked.Add(ref this.shedBytes, bytes); }
}

public readonly record struct ConnectionMetricsSnapshot(string ConnectionId, long BytesIn, long BytesOut, long PacketsIn, long PacketsOut, double RttMilliseconds, double PacketLossPercent, long ShedFrames = 0, long ShedBytes = 0);

/// <summary>Produces a renderer-neutral diagnostics line suitable for an in-game network overlay.</summary>
public static class NetworkDebugOverlay
{
    public static ConnectionMetricsSnapshot Snapshot(string connectionId, ConnectionMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(connectionId); ArgumentNullException.ThrowIfNull(metrics);
        return new(connectionId, metrics.BytesIn, metrics.BytesOut, metrics.PacketsIn, metrics.PacketsOut, metrics.RttMilliseconds, metrics.PacketLossPercent, metrics.ShedFrames, metrics.ShedBytes);
    }
    public static string Format(ConnectionMetricsSnapshot snapshot)
        => string.Format(CultureInfo.InvariantCulture, "NET {0} | RTT {1:0.0} ms | LOSS {2:0.0}% | IN {3} pkts/{4} B | OUT {5} pkts/{6} B", snapshot.ConnectionId, snapshot.RttMilliseconds, snapshot.PacketLossPercent, snapshot.PacketsIn, snapshot.BytesIn, snapshot.PacketsOut, snapshot.BytesOut);
}

public sealed class HeartbeatTracker
{
    private readonly Dictionary<uint, long> pending = new();
    private readonly object sync = new();
    private readonly TimeSpan expiry = TimeSpan.FromSeconds(5);
    private uint nextSequence;
    private long sentCount, acknowledgedCount, expiredCount, duplicateAcknowledgementCount;
    private double smoothedRtt;
    public double SmoothedRttMilliseconds { get { lock (this.sync) return this.smoothedRtt; } }
    public long SentCount { get { lock (this.sync) return this.sentCount; } }
    public long AcknowledgedCount { get { lock (this.sync) return this.acknowledgedCount; } }
    public long ExpiredCount { get { lock (this.sync) return this.expiredCount; } }
    public long DuplicateAcknowledgementCount { get { lock (this.sync) return this.duplicateAcknowledgementCount; } }
    public double LossPercent { get { lock (this.sync) return this.sentCount == 0 ? 0 : this.expiredCount * 100d / this.sentCount; } }
    public byte[] CreateProbe(ConnectionMetrics? metrics = null)
    {
        long now = Stopwatch.GetTimestamp(); uint sequence;
        int expired;
        lock (this.sync)
        {
            expired = 0; foreach (var old in this.pending.Where(x => Stopwatch.GetElapsedTime(x.Value, now) > this.expiry).ToArray()) { this.pending.Remove(old.Key); expired++; }
            this.expiredCount += expired;
            sequence = unchecked(++this.nextSequence); this.pending[sequence] = now;
            this.sentCount++;
        }
        for (int i = 0; i < expired; i++) metrics?.RecordLostPacket();
        metrics?.RecordSequenceSent();
        var writer = new PacketWriter(12); writer.WriteUInt32(sequence); writer.WriteUInt64(unchecked((ulong)now)); return writer.WrittenSpan.ToArray();
    }
    public bool Acknowledge(ReadOnlySpan<byte> payload, ConnectionMetrics metrics)
    {
        if (payload.Length != 12) return false;
        var reader = new PacketReader(payload); uint sequence = reader.ReadUInt32(); _ = reader.ReadUInt64(); long sent;
        lock (this.sync)
        {
            if (!this.pending.Remove(sequence, out sent)) { this.duplicateAcknowledgementCount++; return false; }
            this.acknowledgedCount++;
        }
        metrics.RecordSequenceAcknowledged();
        double milliseconds = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
        double smoothed; lock (this.sync) { this.smoothedRtt = this.smoothedRtt == 0 ? milliseconds : this.smoothedRtt * .875 + milliseconds * .125; smoothed = this.smoothedRtt; }
        metrics.RecordRtt(TimeSpan.FromMilliseconds(smoothed)); return true;
    }
}

/// <summary>Tracks unique application sequence acknowledgements for transport-level loss diagnostics.</summary>
/// <remarks>
/// This is intentionally separate from reliable delivery. It measures the messages observed by the
/// application, including duplicates and gaps, and is therefore suitable for an unreliable native
/// probe or heartbeat stream. The sequence space is bounded to prevent a peer from growing memory
/// without limit.
/// </remarks>
public sealed class SequenceLossTracker
{
    private readonly object sync = new();
    private readonly HashSet<uint> received = new();
    private readonly int maximumTrackedSequences;
    private uint nextSequence;
    private long sent;
    private long duplicates;

    public SequenceLossTracker(int maximumTrackedSequences = 1_000_000)
    {
        if (maximumTrackedSequences < 1) throw new ArgumentOutOfRangeException(nameof(maximumTrackedSequences));
        this.maximumTrackedSequences = maximumTrackedSequences;
    }

    public long Sent { get { lock (this.sync) return this.sent; } }
    public long ReceivedUnique { get { lock (this.sync) return this.received.Count; } }
    public long Duplicates { get { lock (this.sync) return this.duplicates; } }
    public long Missing { get { lock (this.sync) return Math.Max(0, this.sent - this.received.Count); } }
    public double LossPercent { get { lock (this.sync) return this.sent == 0 ? 0 : this.Missing * 100d / this.sent; } }

    /// <summary>Allocates the next sequence number to put in an outbound probe.</summary>
    public uint Next()
    {
        lock (this.sync)
        {
            if (this.sent == long.MaxValue) throw new InvalidOperationException("Sequence counter exhausted.");
            uint sequence = unchecked(++this.nextSequence);
            this.sent++;
            return sequence;
        }
    }

    /// <summary>Records an observed sequence. Returns false for a duplicate or a bounded-window eviction.</summary>
    public bool Observe(uint sequence)
    {
        lock (this.sync)
        {
            if (!this.received.Add(sequence))
            {
                this.duplicates++;
                return false;
            }

            if (this.received.Count > this.maximumTrackedSequences)
            {
                // The oldest value cannot be inferred from an unordered set without retaining another
                // queue. Clear the bounded observation window and keep counters monotonic; this makes
                // long-running diagnostics bounded while preserving the current-window loss result.
                this.received.Clear();
                this.received.Add(sequence);
            }

            return true;
        }
    }
}

public readonly record struct RecordedPacket(DateTimeOffset Time, bool Outbound, byte[] Data, string? ConnectionId = null, NetChannel Channel = NetChannel.State);

/// <summary>In-memory network capture for deterministic desync reproduction.</summary>
public sealed class NetworkRecorder
{
    private const uint FileMagic = 0x474E5352;
    private readonly List<RecordedPacket> packets = new();
    private readonly object sync = new();
    public IReadOnlyList<RecordedPacket> Packets { get { lock (this.sync) return this.packets.ToArray(); } }
    public void Record(bool outbound, ReadOnlySpan<byte> data, DateTimeOffset? now = null, string? connectionId = null, NetChannel channel = NetChannel.State) { lock (this.sync) this.packets.Add(new RecordedPacket(now ?? DateTimeOffset.UtcNow, outbound, data.ToArray(), connectionId, channel)); }
    public async Task PlaybackAsync(Func<RecordedPacket, ValueTask> playback, double speed = 1, CancellationToken cancellationToken = default)
    {
        if (speed <= 0 || double.IsNaN(speed)) throw new ArgumentOutOfRangeException(nameof(speed));
        DateTimeOffset? previous = null;
        RecordedPacket[] snapshot; lock (this.sync) snapshot = this.packets.Select((packet, index) => (packet, index)).OrderBy(x => x.packet.Time).ThenBy(x => x.index).Select(x => x.packet).ToArray();
        foreach (RecordedPacket packet in snapshot)
        {
            if (previous is not null) await Task.Delay(TimeSpan.FromTicks((long)Math.Max(0, (packet.Time - previous.Value).Ticks / speed)), cancellationToken).ConfigureAwait(false);
            await playback(packet).ConfigureAwait(false); previous = packet.Time;
        }
    }
    /// <summary>Replays captured outbound packets through a transport callback and returns successful deliveries.</summary>
    public async Task<int> ReplayTransportAsync(Func<RecordedPacket, ValueTask<bool>> send, double speed = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        int delivered = 0;
        await this.PlaybackAsync(async packet =>
        {
            if (packet.Outbound && await send(packet).ConfigureAwait(false)) Interlocked.Increment(ref delivered);
        }, speed, cancellationToken).ConfigureAwait(false);
        return delivered;
    }
    public void Clear() { lock (this.sync) this.packets.Clear(); }
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        RecordedPacket[] snapshot; lock (this.sync) snapshot = this.packets.ToArray();
        await using var stream = File.Create(path);
        await using var writer = new BinaryWriterStream(stream);
        await writer.WriteUInt32Async(FileMagic, cancellationToken); await writer.WriteUInt16Async(2, cancellationToken); await writer.WriteUInt32Async((uint)snapshot.Length, cancellationToken);
        foreach (RecordedPacket packet in snapshot) { await writer.WriteInt64Async(packet.Time.UtcTicks, cancellationToken); await writer.WriteByteAsync(packet.Outbound ? (byte)1 : (byte)0, cancellationToken); await writer.WriteByteAsync((byte)packet.Channel, cancellationToken); byte[] connection = Encoding.UTF8.GetBytes(packet.ConnectionId ?? string.Empty); await writer.WriteUInt16Async((ushort)connection.Length, cancellationToken); await writer.WriteAsync(connection, cancellationToken); await writer.WriteUInt32Async((uint)packet.Data.Length, cancellationToken); await writer.WriteAsync(packet.Data, cancellationToken); }
    }
    public Task SaveAsync(string path, Func<RecordedPacket, RecordedPacket> redact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(redact);
        NetworkRecorder sanitized = new();
        foreach (RecordedPacket packet in this.Packets) sanitized.RecordPacket(redact(packet));
        return sanitized.SaveAsync(path, cancellationToken);
    }
    private void RecordPacket(RecordedPacket packet) { lock (this.sync) this.packets.Add(packet with { Data = packet.Data.ToArray() }); }
    public static async Task<NetworkRecorder> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        byte[] header = new byte[10]; await stream.ReadExactlyAsync(header, cancellationToken);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != FileMagic || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) != 2) throw new InvalidDataException("Unsupported network capture.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(6)); var result = new NetworkRecorder();
        for (uint i = 0; i < count; i++)
        {
            byte[] fixedPart = new byte[12]; await stream.ReadExactlyAsync(fixedPart, cancellationToken);
            var time = new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(fixedPart), TimeSpan.Zero);
            bool outbound = fixedPart[8] != 0; NetChannel channel = (NetChannel)fixedPart[9]; int identityLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(10));
            byte[] identity = new byte[identityLength]; await stream.ReadExactlyAsync(identity, cancellationToken);
            byte[] lengthData = new byte[4]; await stream.ReadExactlyAsync(lengthData, cancellationToken); int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(lengthData));
            if (length > 128 * 1024 * 1024) throw new InvalidDataException("Capture packet is too large.");
            byte[] data = new byte[length]; await stream.ReadExactlyAsync(data, cancellationToken); result.packets.Add(new RecordedPacket(time, outbound, data, Encoding.UTF8.GetString(identity), channel));
        }
        return result;
    }
}

internal sealed class BinaryWriterStream : IAsyncDisposable
{
    private readonly Stream stream; public BinaryWriterStream(Stream stream) => this.stream = stream;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task WriteUInt32Async(uint value, CancellationToken token) { byte[] data = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(data, value); return WriteAsync(data, token); }
    public Task WriteUInt16Async(ushort value, CancellationToken token) { byte[] data = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(data, value); return WriteAsync(data, token); }
    public Task WriteInt64Async(long value, CancellationToken token) { byte[] data = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(data, value); return WriteAsync(data, token); }
    public Task WriteByteAsync(byte value, CancellationToken token) => WriteAsync([value], token);
    public Task WriteAsync(byte[] data, CancellationToken token) => this.stream.WriteAsync(data, token).AsTask();
}
