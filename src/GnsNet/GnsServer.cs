namespace GnsNet;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using GnsSharp;

/// <summary>
/// An authoritative server listen socket: accepts any number of client connections onto one
/// poll group, and lets you send to one connection or broadcast to all of them.
/// </summary>
/// <remarks>
/// Requires <see cref="GnsRuntime.Initialize"/> to have been called first, and its callback pump
/// to keep running for the lifetime of this server - connection accept/close notifications and
/// incoming messages both depend on it.
/// </remarks>
public sealed class GnsServer : IDisposable
{
    private readonly ISteamNetworkingSockets sockets;
    private readonly HSteamListenSocket listenSocket;
    private readonly HSteamNetPollGroup pollGroup;
    private readonly ConcurrentDictionary<uint, GnsConnection> connections = new();
    private readonly IntPtr[] messageBuffer;

    // Must be kept alive for as long as the listen socket exists: GNS invokes it via a native
    // function pointer (see GnsSharp's README "Steam Callbacks" section on this exception).
    private readonly FnSteamNetConnectionStatusChanged statusChangedCallback;

    private bool disposed;

    private GnsServer(ISteamNetworkingSockets sockets, HSteamListenSocket listenSocket, HSteamNetPollGroup pollGroup, FnSteamNetConnectionStatusChanged statusChangedCallback, int maxMessagesPerPoll)
    {
        this.sockets = sockets;
        this.listenSocket = listenSocket;
        this.pollGroup = pollGroup;
        this.statusChangedCallback = statusChangedCallback;
        this.messageBuffer = new IntPtr[maxMessagesPerPoll];
    }

    /// <summary>Raised when a client connection is accepted.</summary>
    public event Action<GnsConnection>? ClientConnected;

    /// <summary>Raised when a client connection closes, whether by the peer or locally-detected problem.</summary>
    public event Action<GnsConnection, ESteamNetConnectionEnd, string?>? ClientDisconnected;

    /// <summary>Currently connected clients.</summary>
    public IReadOnlyCollection<GnsConnection> Connections => this.connections.Values.ToArray();

    /// <summary>
    /// Starts listening. <paramref name="address"/> is parsed with <c>SteamNetworkingIPAddr.ParseString</c>,
    /// e.g. <c>"[::]:27015"</c> for any address, or <c>"0.0.0.0:27015"</c> for IPv4-only.
    /// </summary>
    /// <param name="address">The address and port to listen on.</param>
    /// <param name="maxMessagesPerPoll">
    /// How many messages a single <see cref="Poll"/> call drains at most. Call <see cref="Poll"/> in a
    /// loop (e.g. once per simulation tick) if you expect more than this many messages per tick.
    /// </param>
    public static GnsServer Listen(string address, int maxMessagesPerPoll = 64)
    {
        ISteamNetworkingSockets sockets = ISteamNetworkingSockets.User
            ?? throw new InvalidOperationException($"{nameof(GnsRuntime)}.{nameof(GnsRuntime.Initialize)} must be called before {nameof(GnsServer)}.{nameof(Listen)}.");

        SteamNetworkingIPAddr addr = default;
        if (!addr.ParseString(address))
        {
            throw new ArgumentException($"Could not parse listen address '{address}'.", nameof(address));
        }

        HSteamNetPollGroup pollGroup = sockets.CreatePollGroup();

        // The status-changed callback needs a reference to the server to update its connection
        // table, but the server can't exist until CreateListenSocketIP returns - so it closes over
        // a mutable cell that's filled in right after.
        GnsServer? server = null;
        FnSteamNetConnectionStatusChanged callback = (ref SteamNetConnectionStatusChangedCallback_t status) =>
        {
            server?.OnConnectionStatusChanged(ref status);
        };

        Span<SteamNetworkingConfigValue_t> configs = stackalloc SteamNetworkingConfigValue_t[1];
        configs[0].SetPtr(ESteamNetworkingConfigValue.Callback_ConnectionStatusChanged, Marshal.GetFunctionPointerForDelegate(callback));

        HSteamListenSocket listenSocket = sockets.CreateListenSocketIP(in addr, configs);
        configs[0].Dispose();

        server = new GnsServer(sockets, listenSocket, pollGroup, callback, maxMessagesPerPoll);
        return server;
    }

    /// <summary>Sends to one connection.</summary>
    public EResult Send(GnsConnection connection, ReadOnlySpan<byte> data, ESteamNetworkingSendType sendType)
        => this.sockets.SendMessageToConnection(connection.Handle, data, sendType);

    /// <summary>Sends to every currently connected client.</summary>
    public void Broadcast(ReadOnlySpan<byte> data, ESteamNetworkingSendType sendType)
    {
        foreach (GnsConnection connection in this.connections.Values)
        {
            this.sockets.SendMessageToConnection(connection.Handle, data, sendType);
        }
    }

    /// <summary>
    /// Drains up to <c>maxMessagesPerPoll</c> (see <see cref="Listen"/>) pending messages across all
    /// connections. Call this once per tick (or however often you need to check for input).
    /// </summary>
    public IReadOnlyList<ReceivedMessage> Poll()
    {
        int count = this.sockets.ReceiveMessagesOnPollGroup(this.pollGroup, this.messageBuffer);
        if (count <= 0)
        {
            return [];
        }

        var received = new List<ReceivedMessage>(count);
        for (int i = 0; i < count; i++)
        {
            nint ptr = this.messageBuffer[i];
            SteamNetworkingMessage_t message = Marshal.PtrToStructure<SteamNetworkingMessage_t>(ptr);

            byte[] data = new byte[message.Size];
            if (message.Size > 0)
            {
                Marshal.Copy(message.Data, data, 0, message.Size);
            }

            SteamNetworkingMessage_t.Release(ptr);

            if (this.connections.TryGetValue(message.Connection.Handle, out GnsConnection? connection))
            {
                received.Add(new ReceivedMessage(connection, data));
            }
        }

        return received;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

        foreach (GnsConnection connection in this.connections.Values)
        {
            this.sockets.CloseConnection(connection.Handle, 0, "Server shutting down", false);
        }

        this.connections.Clear();
        this.sockets.CloseListenSocket(this.listenSocket);
        this.sockets.DestroyPollGroup(this.pollGroup);
    }

    private void OnConnectionStatusChanged(ref SteamNetConnectionStatusChangedCallback_t status)
    {
        switch (status.Info.State)
        {
            case ESteamNetworkingConnectionState.Connecting:
                if (this.sockets.AcceptConnection(status.Conn) == EResult.OK)
                {
                    this.sockets.SetConnectionPollGroup(status.Conn, this.pollGroup);
                    var connection = new GnsConnection(status.Conn);
                    this.connections[status.Conn.Handle] = connection;
                    this.ClientConnected?.Invoke(connection);
                }

                break;

            case ESteamNetworkingConnectionState.ClosedByPeer:
            case ESteamNetworkingConnectionState.ProblemDetectedLocally:
                this.sockets.CloseConnection(status.Conn, 0, null, false);
                if (this.connections.TryRemove(status.Conn.Handle, out GnsConnection? removed))
                {
                    this.ClientDisconnected?.Invoke(removed, (ESteamNetConnectionEnd)status.Info.EndReason, status.Info.EndDebug);
                }

                break;
        }
    }
}
