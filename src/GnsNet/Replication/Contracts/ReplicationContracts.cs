namespace GnsNet.Replication;

using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

/// <summary>Controls which peers may receive and simulate a replicated component.</summary>
public enum ReplicationMode : byte
{
    Interpolated,
    OwnerPredicted,
    OwnerOnly,
    ServerOnly
}

/// <summary>Controls how a field is sampled between two authoritative snapshots.</summary>
public enum InterpolationMode : byte
{
    Auto,
    Step,
    Linear,
    Spherical,
    None
}

/// <summary>Marks a plain .NET type as a component with an explicit stable wire identity.</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ReplicatedComponentAttribute(ushort componentId) : Attribute
{
    /// <summary>Stable component identifier. Never reuse a retired value.</summary>
    public ushort ComponentId { get; } = componentId;
    /// <summary>Version of the component's wire schema.</summary>
    public ushort SchemaVersion { get; init; } = 1;
    /// <summary>Replication authority and presentation policy.</summary>
    public ReplicationMode Mode { get; init; } = ReplicationMode.Interpolated;
    /// <summary>Whether peers must understand this component before admission.</summary>
    public bool Required { get; init; } = true;
}

/// <summary>Marks a component member with an explicit stable field identity.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ReplicatedFieldAttribute(byte fieldId) : Attribute
{
    /// <summary>Stable field identifier within its component. Never reuse a retired value.</summary>
    public byte FieldId { get; } = fieldId;
    /// <summary>Optional positive wire quantization step for floating-point values.</summary>
    public float Quantize { get; init; }
    /// <summary>Optional positive change threshold evaluated before sending a delta.</summary>
    public float Threshold { get; init; }
    /// <summary>Interpolation policy for this member.</summary>
    public InterpolationMode Interpolation { get; init; } = InterpolationMode.Auto;
    /// <summary>Whether receivers that do not know this field may skip it.</summary>
    public bool Optional { get; init; }
}

/// <summary>Explicitly excludes a field or property from replication discovery.</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
public sealed class ReplicatedIgnoreAttribute : Attribute;

/// <summary>Marks an assembly-level registry host for generated replication metadata.</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateReplicationRegistryAttribute : Attribute;

/// <summary>Debugger-friendly immutable field metadata emitted by the replication generator.</summary>
public readonly record struct ReplicationFieldDescriptor(
    byte FieldId,
    string Name,
    string WireType,
    float Quantize,
    float Threshold,
    InterpolationMode Interpolation,
    bool Optional);

/// <summary>Debugger-friendly immutable component metadata emitted by the replication generator.</summary>
public sealed class ReplicationComponentDescriptor
{
    /// <summary>Creates a component descriptor.</summary>
    public ReplicationComponentDescriptor(ushort componentId, ushort schemaVersion, ReplicationMode mode, bool required, string componentType, IReadOnlyList<ReplicationFieldDescriptor> fields)
    {
        ComponentId = componentId;
        SchemaVersion = schemaVersion;
        Mode = mode;
        Required = required;
        ComponentType = componentType ?? throw new ArgumentNullException(nameof(componentType));
        Fields = fields ?? throw new ArgumentNullException(nameof(fields));
    }

    public ushort ComponentId { get; }
    public ushort SchemaVersion { get; }
    public ReplicationMode Mode { get; }
    public bool Required { get; }
    public string ComponentType { get; }
    public IReadOnlyList<ReplicationFieldDescriptor> Fields { get; }
}

/// <summary>Static contract implemented by generated component codecs.</summary>
public interface IReplicatedComponentCodec<T>
{
    static abstract ushort ComponentId { get; }
    static abstract ushort SchemaVersion { get; }
    static abstract GnsNet.DirtyFieldMask Diff(in T baseline, in T current);
    static abstract void WriteFull(ref ReplicationWriter writer, in T value);
    static abstract void WriteDelta(ref ReplicationWriter writer, in T baseline, in T current, in GnsNet.DirtyFieldMask mask);
    static abstract bool TryReadFull(ref ReplicationReader reader, out T value);
    static abstract bool TryApplyDelta(ref ReplicationReader reader, in T baseline, out T value);
    static abstract T Interpolate(in T from, in T to, float alpha);
}

/// <summary>A bounded little-endian writer used by generated fixed-size component codecs.</summary>
public sealed class ReplicationWriter
{
    private readonly ArrayBufferWriter<byte> buffer;

    public ReplicationWriter(int initialCapacity = 128)
    {
        if (initialCapacity < 1) throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        buffer = new ArrayBufferWriter<byte>(initialCapacity);
    }

    public int WrittenCount => buffer.WrittenCount;
    public ReadOnlyMemory<byte> WrittenMemory => buffer.WrittenMemory;
    public byte[] ToArray() => buffer.WrittenMemory.ToArray();
    public void WriteByte(byte value) { Span<byte> span = buffer.GetSpan(1); span[0] = value; buffer.Advance(1); }
    public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));
    public void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);
    public void WriteInt16(short value) { Span<byte> span = buffer.GetSpan(2); BinaryPrimitives.WriteInt16LittleEndian(span, value); buffer.Advance(2); }
    public void WriteUInt16(ushort value) { Span<byte> span = buffer.GetSpan(2); BinaryPrimitives.WriteUInt16LittleEndian(span, value); buffer.Advance(2); }
    public void WriteInt32(int value) { Span<byte> span = buffer.GetSpan(4); BinaryPrimitives.WriteInt32LittleEndian(span, value); buffer.Advance(4); }
    public void WriteUInt32(uint value) { Span<byte> span = buffer.GetSpan(4); BinaryPrimitives.WriteUInt32LittleEndian(span, value); buffer.Advance(4); }
    public void WriteInt64(long value) { Span<byte> span = buffer.GetSpan(8); BinaryPrimitives.WriteInt64LittleEndian(span, value); buffer.Advance(8); }
    public void WriteUInt64(ulong value) { Span<byte> span = buffer.GetSpan(8); BinaryPrimitives.WriteUInt64LittleEndian(span, value); buffer.Advance(8); }
    public void WriteSingle(float value) => WriteInt32(BitConverter.SingleToInt32Bits(value));
    public void WriteDouble(double value) => WriteInt64(BitConverter.DoubleToInt64Bits(value));
    public void WriteVector2(Vector2 value) { WriteSingle(value.X); WriteSingle(value.Y); }
    public void WriteVector3(Vector3 value) { WriteSingle(value.X); WriteSingle(value.Y); WriteSingle(value.Z); }
    public void WriteVector4(Vector4 value) { WriteSingle(value.X); WriteSingle(value.Y); WriteSingle(value.Z); WriteSingle(value.W); }
    public void WriteQuaternion(Quaternion value) { WriteSingle(value.X); WriteSingle(value.Y); WriteSingle(value.Z); WriteSingle(value.W); }
}

/// <summary>A bounds-checked little-endian reader used by generated fixed-size component codecs.</summary>
public ref struct ReplicationReader
{
    private ReadOnlySpan<byte> remaining;
    public ReplicationReader(ReadOnlySpan<byte> payload) => remaining = payload;
    public int Remaining => remaining.Length;
    private bool Take(int count, out ReadOnlySpan<byte> value)
    {
        if (count > remaining.Length) { value = default; return false; }
        value = remaining[..count]; remaining = remaining[count..]; return true;
    }
    public bool TryReadByte(out byte value) { if (!Take(1, out ReadOnlySpan<byte> bytes)) { value = default; return false; } value = bytes[0]; return true; }
    public bool TryReadSByte(out sbyte value) { bool result = TryReadByte(out byte raw); value = unchecked((sbyte)raw); return result; }
    public bool TryReadBoolean(out bool value) { if (!TryReadByte(out byte raw) || raw > 1) { value = default; return false; } value = raw != 0; return true; }
    public bool TryReadInt16(out short value) { if (!Take(2, out ReadOnlySpan<byte> bytes)) { value = default; return false; } value = BinaryPrimitives.ReadInt16LittleEndian(bytes); return true; }
    public bool TryReadUInt16(out ushort value) { if (!Take(2, out ReadOnlySpan<byte> bytes)) { value = default; return false; } value = BinaryPrimitives.ReadUInt16LittleEndian(bytes); return true; }
    public bool TryReadInt32(out int value) { if (!Take(4, out ReadOnlySpan<byte> bytes)) { value = default; return false; } value = BinaryPrimitives.ReadInt32LittleEndian(bytes); return true; }
    public bool TryReadUInt32(out uint value) { if (!Take(4, out ReadOnlySpan<byte> bytes)) { value = default; return false; } value = BinaryPrimitives.ReadUInt32LittleEndian(bytes); return true; }
    public bool TryReadInt64(out long value) { if (!Take(8, out ReadOnlySpan<byte> bytes)) { value = default; return false; } value = BinaryPrimitives.ReadInt64LittleEndian(bytes); return true; }
    public bool TryReadUInt64(out ulong value) { if (!Take(8, out ReadOnlySpan<byte> bytes)) { value = default; return false; } value = BinaryPrimitives.ReadUInt64LittleEndian(bytes); return true; }
    public bool TryReadSingle(out float value) { bool result = TryReadInt32(out int raw); value = BitConverter.Int32BitsToSingle(raw); return result; }
    public bool TryReadDouble(out double value) { bool result = TryReadInt64(out long raw); value = BitConverter.Int64BitsToDouble(raw); return result; }
    public bool TryReadVector2(out Vector2 value) { if (!TryReadSingle(out float x) || !TryReadSingle(out float y)) { value = default; return false; } value = new(x, y); return true; }
    public bool TryReadVector3(out Vector3 value) { if (!TryReadSingle(out float x) || !TryReadSingle(out float y) || !TryReadSingle(out float z)) { value = default; return false; } value = new(x, y, z); return true; }
    public bool TryReadVector4(out Vector4 value) { if (!TryReadSingle(out float x) || !TryReadSingle(out float y) || !TryReadSingle(out float z) || !TryReadSingle(out float w)) { value = default; return false; } value = new(x, y, z, w); return true; }
    public bool TryReadQuaternion(out Quaternion value) { if (!TryReadSingle(out float x) || !TryReadSingle(out float y) || !TryReadSingle(out float z) || !TryReadSingle(out float w)) { value = default; return false; } value = new(x, y, z, w); return true; }
}

/// <summary>Shared canonical comparison, quantization, and interpolation helpers for generated codecs.</summary>
public static class ReplicationMath
{
    public static float Quantize(float value, float step) => step > 0 ? MathF.Round(value / step) * step : value;
    public static double Quantize(double value, float step) => step > 0 ? Math.Round(value / step) * step : value;
    public static Vector2 Quantize(Vector2 value, float step) => new(Quantize(value.X, step), Quantize(value.Y, step));
    public static Vector3 Quantize(Vector3 value, float step) => new(Quantize(value.X, step), Quantize(value.Y, step), Quantize(value.Z, step));
    public static Vector4 Quantize(Vector4 value, float step) => new(Quantize(value.X, step), Quantize(value.Y, step), Quantize(value.Z, step), Quantize(value.W, step));
    public static Quaternion Quantize(Quaternion value, float step) => new(Quantize(value.X, step), Quantize(value.Y, step), Quantize(value.Z, step), Quantize(value.W, step));
    public static bool Equal(float left, float right, float threshold, float quantize) { left = Quantize(left, quantize); right = Quantize(right, quantize); return left.Equals(right) || (threshold > 0 && MathF.Abs(left - right) <= threshold); }
    public static bool Equal(double left, double right, float threshold, float quantize) { left = Quantize(left, quantize); right = Quantize(right, quantize); return left.Equals(right) || (threshold > 0 && Math.Abs(left - right) <= threshold); }
    public static bool Equal(Vector2 left, Vector2 right, float threshold, float quantize) { left = Quantize(left, quantize); right = Quantize(right, quantize); return left.Equals(right) || (threshold > 0 && Vector2.DistanceSquared(left, right) <= threshold * threshold); }
    public static bool Equal(Vector3 left, Vector3 right, float threshold, float quantize) { left = Quantize(left, quantize); right = Quantize(right, quantize); return left.Equals(right) || (threshold > 0 && Vector3.DistanceSquared(left, right) <= threshold * threshold); }
    public static bool Equal(Vector4 left, Vector4 right, float threshold, float quantize) { left = Quantize(left, quantize); right = Quantize(right, quantize); return left.Equals(right) || (threshold > 0 && Vector4.DistanceSquared(left, right) <= threshold * threshold); }
    public static bool Equal(Quaternion left, Quaternion right, float threshold, float quantize)
    {
        left = Quantize(left, quantize); right = Quantize(right, quantize);
        float dot = MathF.Abs(Quaternion.Dot(left, right));
        return (!float.IsNaN(dot) && dot >= 1f - Math.Max(0, threshold)) || left.Equals(right);
    }
}
