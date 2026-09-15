namespace GnsNet.Stride;

using System.Collections.Concurrent;
using System.Threading;

/// <summary>Bounded transport-to-Stride handoff. Obsolete state may be shed; lifecycle is retained whenever possible.</summary>
public sealed class NetworkCommandQueue
{
    private readonly ConcurrentQueue<NetworkCommand> queue = new();
    private readonly int capacity;
    private int count;
    private long droppedState;
    private long rejectedLifecycle;

    /// <summary>Creates a queue with a finite command capacity.</summary>
    public NetworkCommandQueue(int capacity = 4096)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    /// <summary>Number of commands currently waiting for the Stride update thread.</summary>
    public int Count => Volatile.Read(ref this.count);

    /// <summary>Number of state commands dropped under backpressure.</summary>
    public long DroppedStateCount => Interlocked.Read(ref this.droppedState);

    /// <summary>Number of lifecycle commands rejected because the queue contained no shed-able state.</summary>
    public long RejectedLifecycleCount => Interlocked.Read(ref this.rejectedLifecycle);

    /// <summary>Enqueues a command without touching any Stride object.</summary>
    public bool Enqueue(NetworkCommand command)
    {
        while (true)
        {
            int observed = Volatile.Read(ref this.count);
            if (observed >= this.capacity)
            {
                if (!command.IsLifecycle)
                {
                    Interlocked.Increment(ref this.droppedState);
                    return false;
                }

                if (!this.TryDropState())
                {
                    Interlocked.Increment(ref this.rejectedLifecycle);
                    return false;
                }

                continue;
            }

            if (Interlocked.CompareExchange(ref this.count, observed + 1, observed) == observed)
            {
                this.queue.Enqueue(command);
                return true;
            }
        }
    }

    /// <summary>Drains at most the destination length of commands in FIFO order.</summary>
    public int Drain(Span<NetworkCommand> destination)
    {
        int drained = 0;
        while (drained < destination.Length && this.queue.TryDequeue(out NetworkCommand command))
        {
            Interlocked.Decrement(ref this.count);
            destination[drained++] = command;
        }
        return drained;
    }

    /// <summary>Removes all pending commands during scene unload or reconnect cleanup.</summary>
    public void Clear()
    {
        while (this.queue.TryDequeue(out _)) Interlocked.Decrement(ref this.count);
    }

    private bool TryDropState()
    {
        int scan = Volatile.Read(ref this.count);
        while (scan-- > 0 && this.queue.TryDequeue(out NetworkCommand candidate))
        {
            Interlocked.Decrement(ref this.count);
            if (!candidate.IsLifecycle)
            {
                Interlocked.Increment(ref this.droppedState);
                return true;
            }

            this.queue.Enqueue(candidate);
            Interlocked.Increment(ref this.count);
        }
        return false;
    }
}
