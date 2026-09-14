namespace GnsNet;

using System.Buffers.Binary;

/// <summary>Wire codec used by generated replicated-state encoders: schema, dirty mask, then field payloads.</summary>
public static class DirtyFieldMaskCodec
{
    public static byte[] Encode(int schemaVersion, DirtyFieldMask mask, Func<int, byte[]> fieldEncoder)
    {
        if (schemaVersion < 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(mask); ArgumentNullException.ThrowIfNull(fieldEncoder);
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        writer.Write(schemaVersion); writer.Write(mask.Words.Length); foreach (ulong word in mask.Words) writer.Write(word);
        for (int field = 0; field < mask.FieldCount; field++) if (mask.IsSet(field)) { byte[] payload = fieldEncoder(field) ?? throw new InvalidDataException("Generated field encoder returned null."); writer.Write(payload.Length); writer.Write(payload); }
        return stream.ToArray();
    }

    public static (int SchemaVersion, DirtyFieldMask Mask, IReadOnlyDictionary<int, byte[]> Fields) Decode(ReadOnlySpan<byte> data, int expectedSchema, int fieldCount, int maximumFieldBytes = 1024 * 1024)
    {
        if (fieldCount < 1 || maximumFieldBytes < 1) throw new ArgumentOutOfRangeException();
        using var stream = new MemoryStream(data.ToArray()); using var reader = new BinaryReader(stream);
        int schema = reader.ReadInt32(); if (schema != expectedSchema) throw new InvalidDataException("Replication schema mismatch.");
        int words = reader.ReadInt32(); if (words != (fieldCount + 63) / 64) throw new InvalidDataException("Invalid dirty-field mask size.");
        var mask = new DirtyFieldMask(fieldCount); for (int i = 0; i < words; i++) { ulong word = reader.ReadUInt64(); for (int bit = 0; bit < 64 && i * 64 + bit < fieldCount; bit++) if ((word & (1UL << bit)) != 0) mask.Set(i * 64 + bit); }
        var fields = new Dictionary<int, byte[]>(); foreach (int field in Enumerable.Range(0, fieldCount).Where(mask.IsSet)) { int length = reader.ReadInt32(); if (length < 0 || length > maximumFieldBytes || length > stream.Length - stream.Position) throw new InvalidDataException("Invalid generated field payload."); fields[field] = reader.ReadBytes(length); }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing generated field data.");
        return (schema, mask, fields);
    }
}
