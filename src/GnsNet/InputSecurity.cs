namespace GnsNet;

/// <summary>Token-bucket limiter suitable for per-connection message or input budgets.</summary>
public sealed class TokenBucketRateLimiter
{
    private readonly double capacity;
    private readonly double refillPerSecond;
    private double tokens;
    private long lastTimestamp;
    private readonly object sync = new();
    public TokenBucketRateLimiter(double permitsPerSecond, double burst)
    {
        if (permitsPerSecond <= 0 || burst <= 0) throw new ArgumentOutOfRangeException();
        this.capacity = burst; this.refillPerSecond = permitsPerSecond; this.tokens = burst; this.lastTimestamp = Environment.TickCount64;
    }
    public bool TryConsume(double permits = 1)
    {
        if (permits <= 0 || permits > this.capacity) return false;
        lock (this.sync)
        {
            long now = Environment.TickCount64;
            double elapsed = Math.Max(0, now - this.lastTimestamp) / 1000d;
            this.lastTimestamp = now;
            this.tokens = Math.Min(this.capacity, this.tokens + elapsed * this.refillPerSecond);
            if (this.tokens < permits) return false;
            this.tokens -= permits; return true;
        }
    }
}

/// <summary>Per-client input policy: rate limits and game-specific validation run before simulation.</summary>
public sealed class ServerInputGuard<TClientId, TInput> where TClientId : notnull
{
    private readonly Func<TClientId, TInput, bool> validate;
    private readonly Dictionary<TClientId, TokenBucketRateLimiter> limiters = new();
    private readonly object sync = new();
    private readonly double permitsPerSecond;
    private readonly double burst;
    public ServerInputGuard(Func<TClientId, TInput, bool> validate, double inputsPerSecond = 60, double burst = 10)
    { this.validate = validate ?? throw new ArgumentNullException(nameof(validate)); this.permitsPerSecond = inputsPerSecond; this.burst = burst; }
    public bool TryAccept(TClientId client, TInput input)
    {
        TokenBucketRateLimiter limiter; lock (this.sync) { if (!this.limiters.TryGetValue(client, out limiter!)) this.limiters[client] = limiter = new(this.permitsPerSecond, this.burst); }
        if (!limiter.TryConsume()) return false;
        try { return this.validate(client, input); }
        catch (Exception) { return false; }
    }
    public void Remove(TClientId client) { lock (this.sync) this.limiters.Remove(client); }
}

public static class MovementValidation
{
    public static bool IsAllowed((float X, float Y) previous, (float X, float Y) claimed, float elapsedSeconds, float maxSpeed, float tolerance = 0.25f)
    {
        if (elapsedSeconds < 0 || maxSpeed < 0 || float.IsNaN(claimed.X) || float.IsNaN(claimed.Y)) return false;
        float dx = claimed.X - previous.X, dy = claimed.Y - previous.Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        return distance <= maxSpeed * elapsedSeconds + Math.Max(0, tolerance);
    }
}
