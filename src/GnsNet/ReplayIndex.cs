namespace GnsNet;

using System.Text.Json;

public readonly record struct NetworkReplayIndexEntry(string Path, DateTimeOffset FirstPacket, DateTimeOffset LastPacket, int PacketCount, long ByteCount);

/// <summary>Durable metadata index for persisted network captures.</summary>
public sealed class NetworkReplayIndex
{
    private readonly List<NetworkReplayIndexEntry> entries = new();
    public IReadOnlyList<NetworkReplayIndexEntry> Entries => this.entries.ToArray();
    public NetworkReplayIndexEntry Add(string path, NetworkRecorder recorder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path); ArgumentNullException.ThrowIfNull(recorder);
        RecordedPacket[] packets = recorder.Packets.ToArray();
        DateTimeOffset first = packets.Length == 0 ? default : packets.Min(x => x.Time); DateTimeOffset last = packets.Length == 0 ? default : packets.Max(x => x.Time);
        var entry = new NetworkReplayIndexEntry(path, first, last, packets.Length, packets.Sum(x => (long)x.Data.Length)); this.entries.RemoveAll(x => string.Equals(x.Path, path, StringComparison.Ordinal)); this.entries.Add(entry); return entry;
    }
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    { await File.WriteAllTextAsync(path, JsonSerializer.Serialize(this.entries), cancellationToken).ConfigureAwait(false); }
    public static async Task<NetworkReplayIndex> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = new NetworkReplayIndex(); string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        result.entries.AddRange(JsonSerializer.Deserialize<List<NetworkReplayIndexEntry>>(json) ?? []); return result;
    }
}
