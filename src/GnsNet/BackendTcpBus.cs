namespace GnsNet;

using System.Net.Sockets;
using System.Text;
using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

/// <summary>Length-prefixed TCP backend bus for trusted server-to-server traffic.</summary>
public sealed class TcpBackendMessageBus : IBackendMessageBus, IAsyncDisposable
{
    private readonly TcpClient client;
    private Stream stream;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    public TcpBackendMessageBus(TcpClient client) : this(client, client.GetStream()) { }
    private TcpBackendMessageBus(TcpClient client, Stream stream) { this.client = client; this.stream = stream; }
    public static async Task<TcpBackendMessageBus> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    { var client = new TcpClient(); await client.ConnectAsync(host, port, cancellationToken); return new TcpBackendMessageBus(client); }
    public static async Task<TcpBackendMessageBus> ConnectTlsAsync(string host, int port, string targetHost, RemoteCertificateValidationCallback? certificateValidation = null, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient(); await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, certificateValidation);
        try { await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = targetHost, EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 }, cancellationToken).ConfigureAwait(false); return new TcpBackendMessageBus(client, tls); }
        catch { tls.Dispose(); client.Dispose(); throw; }
    }
    public async ValueTask PublishAsync(BackendMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message.Topic) || message.Topic.Length > ushort.MaxValue) throw new ArgumentException("Backend topic is invalid.", nameof(message));
        if (message.Payload.Length > 128 * 1024 * 1024) throw new ArgumentException("Backend payload is too large.", nameof(message));
        byte[] topic = Encoding.UTF8.GetBytes(message.Topic); byte[] packet = new byte[4 + 2 + topic.Length + 8 + 4 + message.Payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, (uint)topic.Length); BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), (ushort)topic.Length); topic.CopyTo(packet, 6); BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(6 + topic.Length), message.CreatedAt.UtcTicks); BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(14 + topic.Length), message.Payload.Length); message.Payload.CopyTo(packet, 18 + topic.Length);
        await this.writeLock.WaitAsync(cancellationToken); try { await this.stream.WriteAsync(packet, cancellationToken); } finally { this.writeLock.Release(); }
    }
    public async IAsyncEnumerable<BackendMessage> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[] header = new byte[6]; await this.stream.ReadExactlyAsync(header, cancellationToken); int topicLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)); byte[] topicData = new byte[topicLength]; await this.stream.ReadExactlyAsync(topicData, cancellationToken); byte[] meta = new byte[12]; await this.stream.ReadExactlyAsync(meta, cancellationToken); int length = BinaryPrimitives.ReadInt32LittleEndian(meta.AsSpan(8)); if (length < 0 || length > 128 * 1024 * 1024) throw new InvalidDataException("Invalid backend message length."); byte[] payload = new byte[length]; await this.stream.ReadExactlyAsync(payload, cancellationToken); string receivedTopic = Encoding.UTF8.GetString(topicData); if (receivedTopic == topic) yield return new BackendMessage(receivedTopic, payload, new DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(meta), TimeSpan.Zero));
        }
    }
    internal async Task AuthenticateServerTlsAsync(X509Certificate2 certificate, CancellationToken cancellationToken)
    {
        NetworkStream network = (NetworkStream)this.stream;
        var tls = new SslStream(network, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate, EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 }, cancellationToken).ConfigureAwait(false);
        this.stream = tls;
    }
    public ValueTask DisposeAsync() { this.stream.Dispose(); this.client.Dispose(); this.writeLock.Dispose(); return ValueTask.CompletedTask; }
}

/// <summary>Accepts trusted backend bus connections for a dedicated backend process.</summary>
public sealed class TcpBackendBusListener : IAsyncDisposable
{
    private readonly TcpListener listener;
    public TcpBackendBusListener(System.Net.IPAddress address, int port) => this.listener = new TcpListener(address, port);
    public void Start() => this.listener.Start();
    public int Port => ((System.Net.IPEndPoint)this.listener.LocalEndpoint).Port;
    public async Task<TcpBackendMessageBus> AcceptAsync(CancellationToken cancellationToken = default) => new(await this.listener.AcceptTcpClientAsync(cancellationToken));
    public async Task<IBackendMessageBus> AcceptAuthenticatedAsync(ReadOnlyMemory<byte> sharedSecret, CancellationToken cancellationToken = default)
    {
        if (sharedSecret.Length < 32) throw new ArgumentException("Backend secret must be at least 256 bits.", nameof(sharedSecret));
        return new ReliableBackendBus(await this.AcceptAsync(cancellationToken), sharedSecret.Span);
    }
    public async Task<IBackendMessageBus> AcceptAuthenticatedTlsAsync(X509Certificate2 certificate, ReadOnlyMemory<byte> sharedSecret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(certificate); if (sharedSecret.Length < 32) throw new ArgumentException("Backend secret must be at least 256 bits.", nameof(sharedSecret));
        TcpBackendMessageBus raw = await this.AcceptAsync(cancellationToken).ConfigureAwait(false);
        await raw.AuthenticateServerTlsAsync(certificate, cancellationToken).ConfigureAwait(false);
        return new ReliableBackendBus(raw, sharedSecret.Span);
    }
    public ValueTask DisposeAsync() { this.listener.Stop(); return ValueTask.CompletedTask; }
}
