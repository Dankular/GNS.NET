namespace GnsNet;

/// <summary>Limits all inbound frames per native connection before application dispatch.</summary>
public sealed class ConnectionRateLimiter
{
    private readonly double perSecond;
    private readonly double burst;
    private readonly Dictionary<uint, TokenBucketRateLimiter> limiters = new();
    private readonly object sync = new();
    public ConnectionRateLimiter(double messagesPerSecond = 120, double burst = 30) { this.perSecond = messagesPerSecond; this.burst = burst; }
    public bool TryAccept(uint connectionHandle)
    {
        TokenBucketRateLimiter limiter; lock (this.sync) { if (!this.limiters.TryGetValue(connectionHandle, out limiter!)) this.limiters[connectionHandle] = limiter = new(this.perSecond, this.burst); }
        return limiter.TryConsume();
    }
    public void Remove(uint connectionHandle) { lock (this.sync) this.limiters.Remove(connectionHandle); }
}
