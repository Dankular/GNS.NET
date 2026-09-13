namespace GnsNet;

using GnsSharp;

/// <summary>Receives validated process-wide GNS configuration values.</summary>
public interface IGnsNativeConfigurationSink
{
    void SetInt32(ESteamNetworkingConfigValue key, int value);
    void SetString(ESteamNetworkingConfigValue key, string value);
}

/// <summary>Maps framework development settings to the native GNS configuration surface.</summary>
public static class GnsNativeConfiguration
{
    public static void Apply(GnsRuntimeOptions options, IGnsNativeConfigurationSink sink)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);
        if (options.Impairment is GnsImpairmentOptions impairment)
        {
            impairment.Validate();
            sink.SetInt32(ESteamNetworkingConfigValue.FakePacketLoss_Send, impairment.LossSendPercent);
            sink.SetInt32(ESteamNetworkingConfigValue.FakePacketLoss_Recv, impairment.LossReceivePercent);
            sink.SetInt32(ESteamNetworkingConfigValue.FakePacketLag_Send, impairment.LagSendMilliseconds);
            sink.SetInt32(ESteamNetworkingConfigValue.FakePacketLag_Recv, impairment.LagReceiveMilliseconds);
            sink.SetInt32(ESteamNetworkingConfigValue.FakePacketJitter_Send_Avg, impairment.JitterSendAverageMilliseconds);
            sink.SetInt32(ESteamNetworkingConfigValue.FakePacketJitter_Send_Max, impairment.JitterSendMaximumMilliseconds);
            sink.SetInt32(ESteamNetworkingConfigValue.FakePacketJitter_Recv_Avg, impairment.JitterReceiveAverageMilliseconds);
            sink.SetInt32(ESteamNetworkingConfigValue.FakePacketJitter_Recv_Max, impairment.JitterReceiveMaximumMilliseconds);
        }
        if (options.P2P is GnsP2POptions p2p)
        {
            p2p.Validate();
            if (p2p.IceCandidatePolicy is int policy) sink.SetInt32(ESteamNetworkingConfigValue.P2P_Transport_ICE_Enable, policy);
            if (p2p.StunServerList is string stun) sink.SetString(ESteamNetworkingConfigValue.P2P_STUN_ServerList, stun);
        }
    }
}
