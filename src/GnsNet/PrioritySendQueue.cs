namespace GnsNet;

/// <summary>Orders outgoing frames by relevance and limits work per tick.</summary>
public sealed class PrioritySendQueue
{
    private readonly List<(NetFrame Frame, NetChannel Channel, float Relevance, long Sequence)> queue = new();
    private readonly int maxFrames;
    private readonly long maxBytes;
    private long queuedBytes;
    private long nextSequence;
    private readonly long starvationThreshold;
    public long ShedBytes { get; private set; }
    public int ShedFrames { get; private set; }
    public int Count => this.queue.Count;
    public long QueuedBytes => this.queuedBytes;
    public long StarvedFrames { get; private set; }
    public PrioritySendQueue(int maxFrames = int.MaxValue, long maxBytes = long.MaxValue, long starvationThreshold = 128)
    {
        if (maxFrames < 1) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (starvationThreshold < 1) throw new ArgumentOutOfRangeException(nameof(starvationThreshold));
        this.maxFrames = maxFrames; this.maxBytes = maxBytes; this.starvationThreshold = starvationThreshold;
    }
    public bool Enqueue(NetFrame frame, NetChannel channel, float relevance)
    {
        ArgumentNullException.ThrowIfNull(frame.Payload);
        long bytes = frame.Encode().LongLength;
        if (this.queue.Count >= this.maxFrames || this.queuedBytes > this.maxBytes - bytes)
        { this.ShedFrames++; this.ShedBytes += bytes; return false; }
        this.queue.Add((frame, channel, Math.Max(0, relevance), this.nextSequence++)); this.queuedBytes += bytes; return true;
    }
    public IReadOnlyList<(NetFrame Frame, NetChannel Channel)> Drain(int maxFrames)
    {
        if (maxFrames < 0) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        var result = new List<(NetFrame, NetChannel)>(Math.Min(maxFrames, this.queue.Count));
        while (result.Count < maxFrames && this.queue.Count != 0)
        {
            long oldest = this.queue.Min(x => x.Sequence);
            int selected = this.queue.FindIndex(x => x.Sequence == oldest && this.nextSequence - x.Sequence >= this.starvationThreshold);
            if (selected >= 0) this.StarvedFrames++;
            else selected = this.queue.Select((x, i) => (x, i)).OrderByDescending(x => x.x.Relevance).ThenBy(x => x.x.Sequence).First().i;
            var item = this.queue[selected]; this.queue.RemoveAt(selected); result.Add((item.Frame, item.Channel)); this.queuedBytes -= item.Frame.Encode().LongLength;
        }
        return result;
    }
}
