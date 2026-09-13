namespace GnsNet;

/// <summary>
/// A message received on a connection, copied out of native memory into a managed buffer.
/// </summary>
public readonly struct ReceivedMessage
{
    internal ReceivedMessage(GnsConnection connection, byte[] data)
    {
        this.Connection = connection;
        this.Data = data;
    }

    /// <summary>Who sent this (on a server, the accepted client; on a client, always the server).</summary>
    public GnsConnection Connection { get; }

    /// <summary>The message payload.</summary>
    public byte[] Data { get; }
}
