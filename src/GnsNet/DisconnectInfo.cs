namespace GnsNet;

public enum GnsDisconnectKind { GracefulQuit, TransportClosed, HeartbeatTimeout }

public readonly record struct DisconnectInfo(GnsDisconnectKind Kind, string? Detail)
{
    public bool PreserveSession => this.Kind != GnsDisconnectKind.GracefulQuit;
}
