namespace GnsNet;

public sealed class GnsImpairmentOptions
{
    public int LossSendPercent { get; init; }
    public int LossReceivePercent { get; init; }
    public int LagSendMilliseconds { get; init; }
    public int LagReceiveMilliseconds { get; init; }
    public int JitterSendAverageMilliseconds { get; init; }
    public int JitterSendMaximumMilliseconds { get; init; }
    public int JitterReceiveAverageMilliseconds { get; init; }
    public int JitterReceiveMaximumMilliseconds { get; init; }
    public void Validate()
    {
        if (LossSendPercent is < 0 or > 100 || LossReceivePercent is < 0 or > 100 || LagSendMilliseconds < 0 || LagReceiveMilliseconds < 0 || JitterSendAverageMilliseconds < 0 || JitterSendMaximumMilliseconds < 0 || JitterReceiveAverageMilliseconds < 0 || JitterReceiveMaximumMilliseconds < 0 || JitterSendMaximumMilliseconds < JitterSendAverageMilliseconds || JitterReceiveMaximumMilliseconds < JitterReceiveAverageMilliseconds) throw new ArgumentOutOfRangeException(nameof(GnsImpairmentOptions));
    }
}
