namespace GnsNet.Stride;

using System.Numerics;

/// <summary>Presentation-only reconciliation policy; authoritative simulation remains outside Stride.</summary>
public sealed class LocalPredictionProcessor
{
    private readonly Dictionary<ulong, Correction> corrections = new();

    /// <summary>Distance at which the visual correction snaps.</summary>
    public float TeleportThreshold { get; init; } = 8f;

    /// <summary>Visual correction decay duration.</summary>
    public TimeSpan SmoothingDuration { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Reconciles presentation error without modifying the simulation position.</summary>
    public bool Reconcile(NetworkEntityView view, Vector3 authoritativePosition, Vector3 predictedPosition)
    {
        Vector3 error = authoritativePosition - predictedPosition;
        if (error.Length() >= this.TeleportThreshold)
        {
            this.corrections.Remove(view.NetworkId);
            NetworkTransformProcessor.ApplyPresentation(view, authoritativePosition, Vector3.Zero);
            return true;
        }

        this.corrections[view.NetworkId] = new Correction(error, Math.Max(this.SmoothingDuration.TotalSeconds, 0.001));
        NetworkTransformProcessor.ApplyPresentation(view, predictedPosition, error);
        return false;
    }

    /// <summary>Advances the visual correction toward zero for one render frame.</summary>
    public void Update(NetworkEntityView view, Vector3 simulationPosition, double elapsedSeconds)
    {
        if (!this.corrections.TryGetValue(view.NetworkId, out Correction correction))
        {
            NetworkTransformProcessor.ApplyPresentation(view, simulationPosition, Vector3.Zero);
            return;
        }

        float amount = (float)Math.Clamp(elapsedSeconds / correction.DurationSeconds, 0, 1);
        Vector3 remaining = correction.Offset * (1 - amount);
        if (remaining.LengthSquared() < 0.000001f) this.corrections.Remove(view.NetworkId);
        else this.corrections[view.NetworkId] = correction with { Offset = remaining };
        NetworkTransformProcessor.ApplyPresentation(view, simulationPosition, remaining);
    }

    /// <summary>Removes correction state after despawn.</summary>
    public void Remove(ulong networkId) => this.corrections.Remove(networkId);

    private readonly record struct Correction(Vector3 Offset, double DurationSeconds);
}
