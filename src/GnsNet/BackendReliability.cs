namespace GnsNet;

using System.Security.Cryptography;
using System.Buffers.Binary;

/// <summary>Authenticated backend envelope and retrying publish decorator.</summary>
public sealed class ReliableBackendBus : IBackendMessageBus, IAsyncDisposable
{
    private readonly IBackendMessageBus inner;
    private readonly byte[] secret;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> outgoing = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> incoming = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> topicLocks = new();
    public ReliableBackendBus(IBackendMessageBus inner, ReadOnlySpan<byte> secret) { this.inner = inner; if (secret.Length < 32) throw new ArgumentException("Backend secret must be at least 256 bits.", nameof(secret)); this.secret = secret.ToArray(); }
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan PublishTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public async ValueTask PublishAsync(BackendMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message.Topic) || message.Topic.Length > 256) throw new ArgumentException("Backend topic is invalid.", nameof(message));
        if (message.Payload.Length > 128 * 1024 * 1024) throw new ArgumentException("Backend payload is too large.", nameof(message));
        SemaphoreSlim topicLock = this.topicLocks.GetOrAdd(message.Topic, static _ => new SemaphoreSlim(1, 1));
        await topicLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        long sequence = this.outgoing.AddOrUpdate(message.Topic, 1, (_, previous) => checked(previous + 1));
        byte[] sequenced = new byte[8 + message.Payload.Length]; BinaryPrimitives.WriteInt64LittleEndian(sequenced, sequence); message.Payload.CopyTo(sequenced, 8);
        byte[] authenticated = BackendAuthentication.Encode(new BackendMessage(message.Topic, sequenced, message.CreatedAt), this.secret);
        var wrapped = new BackendMessage(message.Topic, authenticated, message.CreatedAt);
        Exception? last = null;
        for (int attempt = 1; attempt <= this.MaxAttempts; attempt++)
        {
            try { await this.inner.PublishAsync(wrapped, cancellationToken).AsTask().WaitAsync(this.PublishTimeout, cancellationToken); return; } catch (Exception ex) when (attempt < this.MaxAttempts) { last = ex; await Task.Delay(this.RetryDelay * attempt, cancellationToken); }
        }
        throw new IOException("Backend publish failed after retries.", last);
        }
        finally { topicLock.Release(); }
    }
    public async IAsyncEnumerable<BackendMessage> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (BackendMessage message in this.inner.SubscribeAsync(topic, cancellationToken))
            if (BackendAuthentication.TryDecode(message, this.secret, out BackendMessage verified) && verified.Payload.Length >= 8)
            {
                long sequence = BinaryPrimitives.ReadInt64LittleEndian(verified.Payload);
                long last = this.incoming.GetOrAdd(topic, 0);
                if (sequence > last && this.incoming.TryUpdate(topic, sequence, last)) yield return new BackendMessage(verified.Topic, verified.Payload[8..], verified.CreatedAt);
            }
    }
    public async ValueTask DisposeAsync()
    {
        if (this.inner is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
        foreach (SemaphoreSlim topicLock in this.topicLocks.Values) topicLock.Dispose();
    }
}

internal static class BackendAuthentication
{
    public static byte[] Encode(BackendMessage message, byte[] secret)
    {
        byte[] signature = HMACSHA256.HashData(secret, message.Payload);
        byte[] result = new byte[4 + signature.Length + message.Payload.Length]; BinaryPrimitives.WriteInt32LittleEndian(result, message.Payload.Length); signature.CopyTo(result, 4); message.Payload.CopyTo(result, 4 + signature.Length); return result;
    }
    public static bool TryDecode(BackendMessage message, byte[] secret, out BackendMessage verified)
    {
        verified = default; if (message.Payload.Length < 36) return false; int length = BinaryPrimitives.ReadInt32LittleEndian(message.Payload); if (length < 0 || length != message.Payload.Length - 36) return false; ReadOnlySpan<byte> signature = message.Payload.AsSpan(4, 32); ReadOnlySpan<byte> payload = message.Payload.AsSpan(36); if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(secret, payload))) return false; verified = new BackendMessage(message.Topic, payload.ToArray(), message.CreatedAt); return true;
    }
}
