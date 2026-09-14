namespace GnsNet;

/// <summary>Orders outgoing frames by relevance and limits work per tick.</summary>
public sealed class PrioritySendQueue
{
    private readonly PriorityQueue<(NetFrame Frame, NetChannel Channel), float> queue = new();
    private readonly int maxFrames;
    private readonly long maxBytes;
    private long queuedBytes;
    public long ShedBytes { get; private set; }
    public int ShedFrames { get; private set; }
    public int Count => this.queue.Count;
    public long QueuedBytes => this.queuedBytes;
    public PrioritySendQueue(int maxFrames = int.MaxValue, long maxBytes = long.MaxValue)
    {
        if (maxFrames < 1) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        this.maxFrames = maxFrames; this.maxBytes = maxBytes;
    }
    public bool Enqueue(NetFrame frame, NetChannel channel, float relevance)
    {
        ArgumentNullException.ThrowIfNull(frame.Payload);
        long bytes = frame.Encode().LongLength;
        if (this.queue.Count >= this.maxFrames || this.queuedBytes > this.maxBytes - bytes)
        { this.ShedFrames++; this.ShedBytes += bytes; return false; }
        this.queue.Enqueue((frame, channel), -Math.Max(0, relevance)); this.queuedBytes += bytes; return true;
    }
    public IReadOnlyList<(NetFrame Frame, NetChannel Channel)> Drain(int maxFrames)
    {
        if (maxFrames < 0) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        var result = new List<(NetFrame, NetChannel)>(Math.Min(maxFrames, this.queue.Count));
        while (result.Count < maxFrames && this.queue.TryDequeue(out var item, out _)) { result.Add(item); this.queuedBytes -= item.Frame.Encode().LongLength; }
        return result;
    }
}
