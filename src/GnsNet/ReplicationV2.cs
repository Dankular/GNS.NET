namespace GnsNet;

/// <summary>Stable engine-independent entity identity used by replication protocol v2.</summary>
public readonly record struct NetworkEntityId(ulong Value);
public readonly record struct ReplicationArchetypeId(ushort Value);

public enum EntityRecordKind : byte { Spawn, Despawn, AddComponent, RemoveComponent, FullComponent, DeltaComponent, Ownership }

/// <summary>Configurable bounds applied before a v2 packet allocates decoded payloads.</summary>
public sealed class ReplicationPacketV2Limits
{
    public int MaximumPacketBytes { get; init; } = NetFrame.MaxPayloadBytes;
    public int MaximumRecords { get; init; } = 1024;
    public int MaximumRecordPayloadBytes { get; init; } = 64 * 1024;
    public int MaximumDirtyFields { get; init; } = 4096;
    internal void Validate()
    {
        if (MaximumPacketBytes < 1 || MaximumRecords < 1 || MaximumRecordPayloadBytes < 0 || MaximumDirtyFields < 1)
            throw new ArgumentOutOfRangeException(nameof(ReplicationPacketV2Limits));
    }
}

/// <summary>One bounded component/lifecycle record within a v2 replication packet.</summary>
public sealed record ReplicationRecordV2(EntityRecordKind Kind, NetworkEntityId Entity, ReplicationArchetypeId Archetype,
    ushort ComponentId, ushort SchemaVersion, DirtyFieldMask? DirtyMask, bool Required, byte[] Payload);

/// <summary>
/// Versioned, big-endian replication envelope. It is carried inside the existing <see cref="NetFrame"/>
/// outer framing, so legacy packet decoding remains unchanged.
/// </summary>
public sealed class ReplicationPacketV2
{
    private const uint Magic = 0x474E5332; // GNS2
    private const byte Version = 2;
    public uint ServerTick { get; }
    public uint SnapshotSequence { get; }
    public uint AcknowledgedInputTick { get; }
    public uint? BaselineSequence { get; }
    public IReadOnlyList<ReplicationRecordV2> Records { get; }

    public ReplicationPacketV2(uint serverTick, uint snapshotSequence, uint acknowledgedInputTick, uint? baselineSequence, IReadOnlyList<ReplicationRecordV2> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        this.ServerTick = serverTick; this.SnapshotSequence = snapshotSequence; this.AcknowledgedInputTick = acknowledgedInputTick;
        this.BaselineSequence = baselineSequence; this.Records = records;
    }

    public byte[] Encode(ReplicationPacketV2Limits? limits = null)
    {
        limits ??= new(); limits.Validate();
        if (this.Records.Count > limits.MaximumRecords) throw new InvalidDataException("Replication record count exceeds the configured limit.");
        var writer = new PacketWriter(); writer.WriteUInt32(Magic); writer.WriteByte(Version); writer.WriteUInt32(this.ServerTick);
        writer.WriteUInt32(this.SnapshotSequence); writer.WriteUInt32(this.AcknowledgedInputTick); writer.WriteBool(this.BaselineSequence.HasValue);
        if (this.BaselineSequence is uint baseline) writer.WriteUInt32(baseline);
        WriteVarUInt(writer, (uint)this.Records.Count);
        foreach (ReplicationRecordV2 record in this.Records)
        {
            if (!Enum.IsDefined(record.Kind) || record.Payload is null || record.Payload.Length > limits.MaximumRecordPayloadBytes)
                throw new InvalidDataException("Invalid replication record.");
            writer.WriteByte((byte)record.Kind); writer.WriteUInt64(record.Entity.Value); writer.WriteBool(record.Required);
            if (record.Kind == EntityRecordKind.Spawn) writer.WriteUInt16(record.Archetype.Value);
            if (IsComponentRecord(record.Kind)) { writer.WriteUInt16(record.ComponentId); writer.WriteUInt16(record.SchemaVersion); }
            if (record.Kind == EntityRecordKind.DeltaComponent)
            {
                DirtyFieldMask mask = record.DirtyMask ?? throw new InvalidDataException("Delta records require a dirty mask.");
                if (mask.FieldCount > limits.MaximumDirtyFields) throw new InvalidDataException("Dirty mask exceeds the configured limit.");
                WriteVarUInt(writer, (uint)mask.FieldCount); foreach (ulong word in mask.Words) writer.WriteUInt64(word);
            }
            WriteVarUInt(writer, (uint)record.Payload.Length); writer.WriteBytes(record.Payload);
            if (writer.Length > limits.MaximumPacketBytes) throw new InvalidDataException("Replication packet exceeds the configured limit.");
        }
        return writer.WrittenSpan.ToArray();
    }

    public static ReplicationPacketV2 Decode(ReadOnlySpan<byte> bytes, ReplicationPacketV2Limits? limits = null)
    {
        limits ??= new(); limits.Validate();
        if (bytes.Length > limits.MaximumPacketBytes) throw new InvalidDataException("Replication packet exceeds the configured limit.");
        try
        {
            var reader = new PacketReader(bytes);
            if (reader.ReadUInt32() != Magic || reader.ReadByte() != Version) throw new InvalidDataException("Unsupported replication v2 packet.");
            uint serverTick = reader.ReadUInt32(); uint sequence = reader.ReadUInt32(); uint ackInput = reader.ReadUInt32();
            uint? baseline = reader.ReadBool() ? reader.ReadUInt32() : null;
            uint count = ReadVarUInt(ref reader); if (count > limits.MaximumRecords) throw new InvalidDataException("Replication record count exceeds the configured limit.");
            var records = new List<ReplicationRecordV2>((int)count);
            for (uint index = 0; index < count; index++)
            {
                EntityRecordKind kind = (EntityRecordKind)reader.ReadByte();
                if (!Enum.IsDefined(kind)) throw new InvalidDataException("Unknown replication record kind.");
                NetworkEntityId entity = new(reader.ReadUInt64()); bool required = reader.ReadBool(); ReplicationArchetypeId archetype = default;
                if (kind == EntityRecordKind.Spawn) archetype = new(reader.ReadUInt16());
                ushort component = 0, schema = 0; DirtyFieldMask? mask = null;
                if (IsComponentRecord(kind)) { component = reader.ReadUInt16(); schema = reader.ReadUInt16(); if (component == 0 || schema == 0) throw new InvalidDataException("Invalid component identity."); }
                if (kind == EntityRecordKind.DeltaComponent)
                {
                    uint fields = ReadVarUInt(ref reader); if (fields is 0 or > 4096 || fields > limits.MaximumDirtyFields) throw new InvalidDataException("Invalid dirty field count.");
                    mask = new DirtyFieldMask((int)fields);
                    int words = ((int)fields + 63) / 64;
                    for (int word = 0; word < words; word++)
                    {
                        ulong value = reader.ReadUInt64();
                        if (word == words - 1 && fields % 64 != 0 && (value >> ((int)fields % 64)) != 0) throw new InvalidDataException("Dirty mask has out-of-range bits.");
                        for (int bit = 0; bit < 64 && word * 64 + bit < fields; bit++) if ((value & (1UL << bit)) != 0) mask.Set(word * 64 + bit);
                    }
                }
                uint length = ReadVarUInt(ref reader); if (length > limits.MaximumRecordPayloadBytes || length > reader.Remaining) throw new InvalidDataException("Invalid replication record payload length.");
                records.Add(new(kind, entity, archetype, component, schema, mask, required, reader.ReadBytes((int)length).ToArray()));
            }
            if (reader.Remaining != 0) throw new InvalidDataException("Trailing replication packet data.");
            return new(serverTick, sequence, ackInput, baseline, records);
        }
        catch (OverflowException exception) { throw new InvalidDataException("Invalid replication varint.", exception); }
    }

    private static bool IsComponentRecord(EntityRecordKind kind) => kind is EntityRecordKind.AddComponent or EntityRecordKind.RemoveComponent or EntityRecordKind.FullComponent or EntityRecordKind.DeltaComponent;
    private static void WriteVarUInt(PacketWriter writer, uint value) { while (value >= 0x80) { writer.WriteByte((byte)(value | 0x80)); value >>= 7; } writer.WriteByte((byte)value); }
    private static uint ReadVarUInt(ref PacketReader reader)
    {
        uint value = 0;
        for (int shift = 0; shift < 35; shift += 7) { byte next = reader.ReadByte(); if (shift == 28 && (next & 0xF0) != 0) throw new InvalidDataException("Invalid replication varint."); value |= (uint)(next & 0x7F) << shift; if ((next & 0x80) == 0) return value; }
        throw new InvalidDataException("Invalid replication varint.");
    }
}

public readonly record struct ReplicationBudget(int MaximumBytes, int MaximumRecords)
{
    public void Validate() { if (MaximumBytes < 1 || MaximumRecords < 1) throw new ArgumentOutOfRangeException(nameof(ReplicationBudget)); }
}
public readonly record struct SnapshotAck(uint SnapshotSequence, uint? AcknowledgedInputTick = null);
public interface IReplicationPriorityPolicy<TClientId> where TClientId : notnull { float Score(TClientId observer, NetworkEntityId entity, ushort componentId, uint tick); }

/// <summary>Register a typed codec to enable the optional typed convenience APIs on <see cref="ReplicationWorld{TClientId}"/>.</summary>
public interface IReplicationComponentCodec<T>
{
    ushort ComponentId { get; }
    ushort SchemaVersion { get; }
    bool Required { get; }
    byte[] EncodeFull(in T value);
}

/// <summary>Authoritative component store used by the component snapshot scheduler.</summary>
public sealed class ReplicationWorld<TClientId> where TClientId : notnull
{
    internal sealed class EntityState(ReplicationArchetypeId archetype, object? owner) { internal readonly Dictionary<ushort, ComponentState> Components = new(); internal readonly Dictionary<Type, object> TypedComponents = new(); internal ReplicationArchetypeId Archetype = archetype; internal object? Owner = owner; }
    internal sealed record ComponentState(ushort SchemaVersion, byte[] FullPayload, DirtyFieldMask? DeltaMask, byte[]? DeltaPayload, bool Required);
    private readonly Dictionary<NetworkEntityId, EntityState> entities = new();
    private readonly Dictionary<Type, object> codecs = new();
    internal IReadOnlyDictionary<NetworkEntityId, EntityState> Entities => this.entities;

    public void Register<T>(IReplicationComponentCodec<T> codec) { ArgumentNullException.ThrowIfNull(codec); if (codec.ComponentId == 0 || codec.SchemaVersion == 0) throw new ArgumentOutOfRangeException(nameof(codec)); this.codecs.Add(typeof(T), codec); }
    public void Spawn(NetworkEntityId entity, ReplicationArchetypeId archetype, object? owner = null)
    {
        if (entity.Value == 0 || archetype.Value == 0) throw new ArgumentOutOfRangeException(nameof(entity));
        if (!this.entities.TryAdd(entity, new(archetype, owner))) throw new InvalidOperationException("Network entity already exists.");
    }
    public bool Despawn(NetworkEntityId entity) => this.entities.Remove(entity);
    public void Set<T>(NetworkEntityId entity, in T component)
    {
        if (!this.codecs.TryGetValue(typeof(T), out object? untyped) || untyped is not IReplicationComponentCodec<T> codec) throw new InvalidOperationException($"No replication codec is registered for {typeof(T)}.");
        this.SetRaw(entity, codec.ComponentId, codec.SchemaVersion, codec.EncodeFull(component), required: codec.Required);
        this.entities[entity].TypedComponents[typeof(T)] = component!;
    }
    public void SetRaw(NetworkEntityId entity, ushort componentId, ushort schemaVersion, ReadOnlySpan<byte> fullPayload, DirtyFieldMask? deltaMask = null, ReadOnlySpan<byte> deltaPayload = default, bool required = true)
    {
        if (componentId == 0 || schemaVersion == 0 || fullPayload.Length > NetFrame.MaxPayloadBytes) throw new ArgumentOutOfRangeException(nameof(componentId));
        if (!this.entities.TryGetValue(entity, out EntityState? state)) throw new InvalidOperationException("Network entity does not exist.");
        if (deltaMask is not null && deltaPayload.Length == 0) throw new ArgumentException("A delta mask requires a delta payload.", nameof(deltaPayload));
        state.Components[componentId] = new(schemaVersion, fullPayload.ToArray(), deltaMask, deltaMask is null ? null : deltaPayload.ToArray(), required);
    }
    public bool Remove<T>(NetworkEntityId entity) => this.codecs.TryGetValue(typeof(T), out object? codec) && codec is IReplicationComponentCodec<T> typed && this.RemoveRaw(entity, typed.ComponentId);
    public bool RemoveRaw(NetworkEntityId entity, ushort componentId) => this.entities.TryGetValue(entity, out EntityState? state) && state.Components.Remove(componentId);
    public bool TryGet<T>(NetworkEntityId entity, out T component)
    {
        if (this.entities.TryGetValue(entity, out EntityState? state) && state.TypedComponents.TryGetValue(typeof(T), out object? value) && value is T typed) { component = typed; return true; }
        component = default!; return false;
    }
}

/// <summary>Per-observer v2 scheduler with reliable lifecycle ordering and acknowledgement-based baselines.</summary>
public sealed class ComponentSnapshotScheduler<TClientId> where TClientId : notnull
{
    private readonly record struct ComponentKey(NetworkEntityId Entity, ushort ComponentId);
    private sealed record Queued(ReplicationRecordV2 Record, bool Reliable, ComponentKey? CommitKey, byte[]? CommitPayload);
    private sealed class Observer
    {
        internal readonly HashSet<NetworkEntityId> KnownEntities = new(); internal readonly HashSet<ComponentKey> KnownComponents = new();
        internal readonly Dictionary<ComponentKey, byte[]> Baselines = new(); internal readonly HashSet<ComponentKey> InFlight = new();
        internal readonly List<Queued> Queue = new(); internal readonly Dictionary<uint, List<Queued>> AwaitingAcknowledgement = new();
        internal uint NextSequence;
    }
    private readonly ReplicationWorld<TClientId> world;
    private readonly Dictionary<TClientId, Observer> observers = new();
    private Func<TClientId, IEnumerable<NetworkEntityId>>? visibility;
    private readonly IReplicationPriorityPolicy<TClientId>? priority;
    private readonly byte opcode;
    private uint tick;
    public long DeferredRecords { get; private set; }
    public ComponentSnapshotScheduler(ReplicationWorld<TClientId> world, byte opcode, IReplicationPriorityPolicy<TClientId>? priority = null) { this.world = world ?? throw new ArgumentNullException(nameof(world)); this.opcode = opcode; this.priority = priority; }
    public void AddClient(TClientId client) => this.observers.TryAdd(client, new());
    public void RemoveClient(TClientId client) => this.observers.Remove(client);
    public void ConfigureVisibility(Func<TClientId, IEnumerable<NetworkEntityId>> provider) => this.visibility = provider ?? throw new ArgumentNullException(nameof(provider));

    public void Tick(uint authoritativeTick)
    {
        if (this.visibility is null) throw new InvalidOperationException("Visibility is not configured."); this.tick = authoritativeTick;
        foreach ((TClientId client, Observer observer) in this.observers)
        {
            var visible = new HashSet<NetworkEntityId>(this.visibility(client) ?? throw new InvalidOperationException("Visibility provider returned null."));
            foreach (NetworkEntityId entity in observer.KnownEntities.Where(entity => !visible.Contains(entity) || !this.world.Entities.ContainsKey(entity)).ToArray()) this.QueueDespawn(observer, entity);
            foreach (NetworkEntityId entity in visible.OrderBy(id => id.Value)) if (this.world.Entities.TryGetValue(entity, out ReplicationWorld<TClientId>.EntityState? state)) this.ScheduleEntity(observer, entity, state, client);
        }
    }

    public IReadOnlyList<(NetFrame Frame, NetChannel Channel)> Drain(TClientId client, ReplicationBudget budget)
    {
        budget.Validate(); if (!this.observers.TryGetValue(client, out Observer? observer) || observer.Queue.Count == 0) return [];
        bool reliable = observer.Queue.Any(x => x.Reliable);
        List<Queued> candidates = reliable ? observer.Queue.Where(x => x.Reliable).ToList() : observer.Queue.Where(x => !x.Reliable).OrderByDescending(x => this.priority?.Score(client, x.Record.Entity, x.Record.ComponentId, this.tick) ?? 0).ToList();
        var selected = new List<Queued>(); byte[]? encoded = null;
        foreach (Queued candidate in candidates)
        {
            if (selected.Count == budget.MaximumRecords) break;
            var attempt = new List<Queued>(selected) { candidate };
            byte[] packet = new ReplicationPacketV2(this.tick, unchecked(observer.NextSequence + 1), 0, null, attempt.Select(x => x.Record).ToArray()).Encode();
            if (packet.Length > budget.MaximumBytes) { if (selected.Count == 0) this.DeferredRecords++; break; }
            selected.Add(candidate); encoded = packet;
        }
        if (selected.Count == 0 || encoded is null) return [];
        observer.NextSequence++; foreach (Queued item in selected) observer.Queue.Remove(item);
        List<Queued> commits = selected.Where(x => x.CommitKey.HasValue).ToList(); if (commits.Count != 0) observer.AwaitingAcknowledgement[observer.NextSequence] = commits;
        return [new(new NetFrame(this.opcode, this.tick, encoded), reliable ? NetChannel.Event : NetChannel.State)];
    }

    public void Acknowledge(TClientId client, SnapshotAck ack)
    {
        if (!this.observers.TryGetValue(client, out Observer? observer)) return;
        foreach (uint sequence in observer.AwaitingAcknowledgement.Keys.Where(sequence => !TickSequence.IsNewer(ack.SnapshotSequence, sequence)).ToArray())
        {
            foreach (Queued commit in observer.AwaitingAcknowledgement[sequence]) if (commit.CommitKey is ComponentKey key && commit.CommitPayload is byte[] payload && observer.KnownComponents.Contains(key)) { observer.Baselines[key] = payload; observer.InFlight.Remove(key); }
            observer.AwaitingAcknowledgement.Remove(sequence);
        }
    }

    private void ScheduleEntity(Observer observer, NetworkEntityId entity, ReplicationWorld<TClientId>.EntityState state, TClientId client)
    {
        bool spawned = observer.KnownEntities.Add(entity);
        if (spawned) this.Enqueue(observer, new(EntityRecordKind.Spawn, entity, state.Archetype, 0, 0, null, true, []), true, null, null);
        foreach ((ushort id, ReplicationWorld<TClientId>.ComponentState component) in state.Components.OrderBy(x => x.Key))
        {
            var key = new ComponentKey(entity, id); bool added = observer.KnownComponents.Add(key);
            if (added) this.Enqueue(observer, new(EntityRecordKind.AddComponent, entity, default, id, component.SchemaVersion, null, component.Required, []), true, null, null);
            if (observer.InFlight.Contains(key)) continue;
            if (!observer.Baselines.TryGetValue(key, out byte[]? baseline) || !baseline.AsSpan().SequenceEqual(component.FullPayload))
            {
                bool delta = baseline is not null && component.DeltaMask is not null && component.DeltaPayload is not null;
                this.Enqueue(observer, new(delta ? EntityRecordKind.DeltaComponent : EntityRecordKind.FullComponent, entity, default, id, component.SchemaVersion,
                    delta ? component.DeltaMask : null, component.Required, delta ? component.DeltaPayload! : component.FullPayload), spawned || added, key, component.FullPayload);
                observer.InFlight.Add(key);
            }
        }
        foreach (ComponentKey removed in observer.KnownComponents.Where(key => key.Entity == entity && !state.Components.ContainsKey(key.ComponentId)).ToArray())
        {
            observer.KnownComponents.Remove(removed); observer.Baselines.Remove(removed); observer.InFlight.Remove(removed);
            this.Enqueue(observer, new(EntityRecordKind.RemoveComponent, entity, default, removed.ComponentId, 1, null, true, []), true, null, null);
        }
    }
    private void QueueDespawn(Observer observer, NetworkEntityId entity)
    {
        observer.KnownEntities.Remove(entity); foreach (ComponentKey key in observer.KnownComponents.Where(key => key.Entity == entity).ToArray()) { observer.KnownComponents.Remove(key); observer.Baselines.Remove(key); observer.InFlight.Remove(key); }
        this.Enqueue(observer, new(EntityRecordKind.Despawn, entity, default, 0, 0, null, true, []), true, null, null);
    }
    private void Enqueue(Observer observer, ReplicationRecordV2 record, bool reliable, ComponentKey? commitKey, byte[]? commitPayload)
    {
        if (commitKey is ComponentKey key && observer.Queue.Any(item => item.CommitKey == key)) return;
        observer.Queue.Add(new(record, reliable, commitKey, commitPayload));
    }
}
