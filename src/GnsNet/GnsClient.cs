namespace GnsNet;

using System.Runtime.InteropServices;
using GnsSharp;

/// <summary>
/// A single outbound connection to a <see cref="GnsServer"/>.
/// </summary>
/// <remarks>
/// Requires <see cref="GnsRuntime.Initialize"/> to have been called first, and its callback pump
/// to keep running for the lifetime of this client - connect/disconnect notifications and incoming
/// messages both depend on it.
/// </remarks>
public sealed class GnsClient : IDisposable
{
    private readonly ISteamNetworkingSockets sockets;
    private readonly HSteamNetConnection connectionHandle;
    private readonly IntPtr[] messageBuffer;

    // Must be kept alive for as long as the connection exists - see GnsServer's callback field.
    private readonly FnSteamNetConnectionStatusChanged statusChangedCallback;

    private bool disposed;
    /// <summary>Connection admission policy. Set before the asynchronous connection reaches Connected.</summary>
    public TransportSecurityPolicy SecurityPolicy { get; set; } = new();

    private GnsClient(ISteamNetworkingSockets sockets, HSteamNetConnection connectionHandle, FnSteamNetConnectionStatusChanged statusChangedCallback, int maxMessagesPerPoll)
    {
        this.sockets = sockets;
        this.connectionHandle = connectionHandle;
        this.statusChangedCallback = statusChangedCallback;
        this.messageBuffer = new IntPtr[maxMessagesPerPoll];
        this.Connection = new GnsConnection(connectionHandle);
    }

    /// <summary>Raised once the connection reaches the <c>Connected</c> state.</summary>
    public event Action? Connected;

    /// <summary>Raised when the connection closes, whether by the peer or locally-detected problem.</summary>
    public event Action<ESteamNetConnectionEnd, string?>? Disconnected;

    /// <summary>The connection to the server, usable with <see cref="Send"/>.</summary>
    public GnsConnection Connection { get; }

    /// <summary>
    /// Begins connecting to <paramref name="address"/> (e.g. <c>"127.0.0.1:27015"</c>). Connection
    /// establishment is asynchronous - subscribe to <see cref="Connected"/> to know when it's ready.
    /// </summary>
    public static GnsClient Connect(string address, int maxMessagesPerPoll = 64)
    {
        ISteamNetworkingSockets sockets = ISteamNetworkingSockets.User
            ?? throw new InvalidOperationException($"{nameof(GnsRuntime)}.{nameof(GnsRuntime.Initialize)} must be called before {nameof(GnsClient)}.{nameof(Connect)}.");

        SteamNetworkingIPAddr addr = default;
        if (!addr.ParseString(address))
        {
            throw new ArgumentException($"Could not parse address '{address}'.", nameof(address));
        }

        GnsClient? client = null;
        FnSteamNetConnectionStatusChanged callback = (ref SteamNetConnectionStatusChangedCallback_t status) =>
        {
            client?.OnConnectionStatusChanged(ref status);
        };

        Span<SteamNetworkingConfigValue_t> configs = stackalloc SteamNetworkingConfigValue_t[1];
        configs[0].SetPtr(ESteamNetworkingConfigValue.Callback_ConnectionStatusChanged, Marshal.GetFunctionPointerForDelegate(callback));

        HSteamNetConnection connectionHandle = sockets.ConnectByIPAddress(in addr, configs);
        configs[0].Dispose();

        client = new GnsClient(sockets, connectionHandle, callback, maxMessagesPerPoll);
        return client;
    }

    /// <summary>Begins a native GNS P2P connection, allowing GNS to negotiate ICE/SDR paths.</summary>
    /// <param name="remoteIdentity">GNS identity string of the remote peer.</param>
    /// <param name="virtualPort">Virtual port selected by the listening P2P socket.</param>
    /// <param name="maxMessagesPerPoll">Maximum messages retained by each poll call.</param>
    public static GnsClient ConnectP2P(string remoteIdentity, int virtualPort = 0, int maxMessagesPerPoll = 64)
    {
        if (string.IsNullOrWhiteSpace(remoteIdentity)) throw new ArgumentException("A remote GNS identity is required.", nameof(remoteIdentity));
        if (virtualPort < 0) throw new ArgumentOutOfRangeException(nameof(virtualPort));
        ISteamNetworkingSockets sockets = ISteamNetworkingSockets.User
            ?? throw new InvalidOperationException($"{nameof(GnsRuntime)}.{nameof(GnsRuntime.Initialize)} must be called before connecting.");
        SteamNetworkingIdentity identity = default;
        if (!identity.ParseString(remoteIdentity)) throw new ArgumentException($"Could not parse GNS identity '{remoteIdentity}'.", nameof(remoteIdentity));
        GnsClient? client = null;
        FnSteamNetConnectionStatusChanged callback = (ref SteamNetConnectionStatusChangedCallback_t status) => client?.OnConnectionStatusChanged(ref status);
        Span<SteamNetworkingConfigValue_t> configs = stackalloc SteamNetworkingConfigValue_t[1];
        configs[0].SetPtr(ESteamNetworkingConfigValue.Callback_ConnectionStatusChanged, Marshal.GetFunctionPointerForDelegate(callback));
        HSteamNetConnection connectionHandle = sockets.ConnectP2P(in identity, virtualPort, configs);
        configs[0].Dispose();
        client = new GnsClient(sockets, connectionHandle, callback, maxMessagesPerPoll);
        return client;
    }

    /// <summary>Sends to the server.</summary>
    public EResult Send(ReadOnlySpan<byte> data, ESteamNetworkingSendType sendType)
        => this.sockets.SendMessageToConnection(this.connectionHandle, data, sendType);

    public void Close(string reason = "Client quit")
    {
        if (this.disposed) return;
        this.disposed = true;
        this.sockets.CloseConnection(this.connectionHandle, 0, reason, false);
    }

    /// <summary>
    /// Drains up to <c>maxMessagesPerPoll</c> (see <see cref="Connect"/>) pending messages from the
    /// server. Call this once per tick (or however often you need to check for input).
    /// </summary>
    public IReadOnlyList<ReceivedMessage> Poll()
    {
        int count = this.sockets.ReceiveMessagesOnConnection(this.connectionHandle, this.messageBuffer);
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
            received.Add(new ReceivedMessage(this.Connection, data));
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
        this.sockets.CloseConnection(this.connectionHandle, 0, "Client disposed", false);
    }

    private void OnConnectionStatusChanged(ref SteamNetConnectionStatusChangedCallback_t status)
    {
        switch (status.Info.State)
        {
            case ESteamNetworkingConnectionState.Connected:
                if (!this.SecurityPolicy.Accept(status.Info, out string? securityReason))
                {
                    this.sockets.CloseConnection(status.Conn, 1, securityReason, false);
                    this.Disconnected?.Invoke(ESteamNetConnectionEnd.Local_NetworkConfig, securityReason);
                    break;
                }
                this.Connected?.Invoke();
                break;

            case ESteamNetworkingConnectionState.ClosedByPeer:
            case ESteamNetworkingConnectionState.ProblemDetectedLocally:
                this.sockets.CloseConnection(status.Conn, 0, null, false);
                this.Disconnected?.Invoke((ESteamNetConnectionEnd)status.Info.EndReason, status.Info.EndDebug);
                break;
        }
    }
}
