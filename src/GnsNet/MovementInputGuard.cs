namespace GnsNet;

/// <summary>Stateful anti-speedhack/anti-teleport validation for authoritative input streams.</summary>
public sealed class MovementInputGuard<TClientId> where TClientId : notnull
{
    private readonly float maxSpeed;
    private readonly float tolerance;
    private readonly Dictionary<TClientId, ((float X, float Y) Position, DateTimeOffset Time)> last = new();
    private readonly object sync = new();
    public MovementInputGuard(float maxSpeed, float tolerance = 0.25f) { if (maxSpeed < 0) throw new ArgumentOutOfRangeException(nameof(maxSpeed)); this.maxSpeed = maxSpeed; this.tolerance = tolerance; }
    /// <summary>Seeds the last position from authoritative server state.</summary>
    public void SetAuthoritativePosition(TClientId client, (float X, float Y) position, DateTimeOffset? now = null)
    { lock (this.sync) this.last[client] = (position, now ?? DateTimeOffset.UtcNow); }
    public bool Accept(TClientId client, (float X, float Y) claimedPosition, DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        lock (this.sync)
        {
            if (!this.last.TryGetValue(client, out var previous)) return false;
            bool allowed = MovementValidation.IsAllowed(previous.Position, claimedPosition, (float)(timestamp - previous.Time).TotalSeconds, this.maxSpeed, this.tolerance);
            if (allowed) this.last[client] = (claimedPosition, timestamp);
            return allowed;
        }
    }
    public void Remove(TClientId client) { lock (this.sync) this.last.Remove(client); }
}
