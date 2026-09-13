namespace GnsNet;

/// <summary>
/// Wraparound-safe comparison for tick/sequence counters, per the RFC 1982 serial number
/// arithmetic pattern. Plain <c>candidate > baseline</c> breaks once a <c>uint</c> tick counter
/// wraps back to 0; this doesn't.
/// </summary>
public static class TickSequence
{
    /// <summary>
    /// True if <paramref name="candidate"/> is strictly newer than <paramref name="baseline"/>.
    /// </summary>
    public static bool IsNewer(uint baseline, uint candidate) => unchecked((int)(candidate - baseline)) > 0;
}
