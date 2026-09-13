namespace GnsNet;

/// <summary>Orders outgoing frames by relevance and limits work per tick.</summary>
public sealed class PrioritySendQueue
{
    private readonly PriorityQueue<(NetFrame Frame, NetChannel Channel), float> queue = new();
    public int Count => this.queue.Count;
    public void Enqueue(NetFrame frame, NetChannel channel, float relevance)
        => this.queue.Enqueue((frame, channel), -Math.Max(0, relevance));
    public IReadOnlyList<(NetFrame Frame, NetChannel Channel)> Drain(int maxFrames)
    {
        if (maxFrames < 0) throw new ArgumentOutOfRangeException(nameof(maxFrames));
        var result = new List<(NetFrame, NetChannel)>(Math.Min(maxFrames, this.queue.Count));
        while (result.Count < maxFrames && this.queue.TryDequeue(out var item, out _)) result.Add(item);
        return result;
    }
}
