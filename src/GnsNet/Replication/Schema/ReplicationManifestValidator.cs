namespace GnsNet.Replication.Schema;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Checked-in wire-contract history for replication protocol v2.</summary>
public sealed class ReplicationManifest
{
    public int ReplicationProtocol { get; init; }
    public List<ReplicationManifestComponent> Components { get; init; } = [];
    public List<int> ReservedComponentIds { get; init; } = [];
}

/// <summary>One explicitly identified replicated component in a manifest.</summary>
public sealed class ReplicationManifestComponent
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Schema { get; init; }
    public ReplicationMode Mode { get; init; } = ReplicationMode.Interpolated;
    public bool Required { get; init; } = true;
    public List<ReplicationManifestField> Fields { get; init; } = [];
    public List<int> ReservedFieldIds { get; init; } = [];
}

/// <summary>One explicitly identified replicated field in a manifest.</summary>
public sealed class ReplicationManifestField
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string WireType { get; init; } = string.Empty;
    public float Quantize { get; init; }
    public float Threshold { get; init; }
    public InterpolationMode Interpolation { get; init; } = InterpolationMode.Auto;
    public bool Optional { get; init; }
}

/// <summary>Deterministic result returned by manifest validation and metadata comparison.</summary>
public sealed class ReplicationManifestValidationResult
{
    internal ReplicationManifestValidationResult(IEnumerable<string> errors) => Errors = errors.OrderBy(static error => error, StringComparer.Ordinal).ToArray();
    public IReadOnlyList<string> Errors { get; }
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Validates bounded replication manifests and prevents accidental wire-identity reuse. This is a
/// build-time/tooling API; it is deliberately outside the encode/decode hot path.
/// </summary>
public static class ReplicationManifestValidator
{
    public const int CurrentProtocol = 2;
    public const int MaximumManifestBytes = 1_048_576;
    public const int MaximumComponents = 4_096;
    public const int MaximumFieldsPerComponent = 255;
    public const int MaximumNameLength = 512;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Parses a manifest after applying the configured input bound.</summary>
    public static ReplicationManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaximumManifestBytes)
            throw new InvalidDataException($"Replication manifest exceeds {MaximumManifestBytes} bytes.");
        try
        {
            return JsonSerializer.Deserialize<ReplicationManifest>(json, JsonOptions)
                ?? throw new InvalidDataException("Replication manifest must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Replication manifest is not valid JSON.", exception);
        }
    }

    /// <summary>Reads and parses a bounded UTF-8 manifest file.</summary>
    public static ReplicationManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Replication manifest was not found.", path);
        if (info.Length > MaximumManifestBytes) throw new InvalidDataException($"Replication manifest exceeds {MaximumManifestBytes} bytes.");
        return Parse(File.ReadAllText(path));
    }

    /// <summary>Writes canonical indented JSON suitable for source control.</summary>
    public static string ToJson(ReplicationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonOptions) { WriteIndented = true });
    }

    /// <summary>Validates one manifest's bounded structure and identity uniqueness.</summary>
    public static ReplicationManifestValidationResult Validate(ReplicationManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var errors = new List<string>();
        if (manifest.ReplicationProtocol != CurrentProtocol) errors.Add($"replicationProtocol must be {CurrentProtocol}.");
        if (manifest.Components is null || manifest.ReservedComponentIds is null) return new(errors.Append("components and reservedComponentIds are required arrays."));
        if (manifest.Components.Count > MaximumComponents) errors.Add($"components exceeds the limit of {MaximumComponents}.");

        var componentIds = new HashSet<int>();
        var componentNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ReplicationManifestComponent component in manifest.Components)
        {
            if (component is null) { errors.Add("components cannot contain null."); continue; }
            string prefix = $"component {component.Id}";
            if (component.Id is < 1 or > ushort.MaxValue) errors.Add($"{prefix} has an invalid ID.");
            else if (!componentIds.Add(component.Id)) errors.Add($"component ID {component.Id} is duplicated.");
            if (!ValidName(component.Name)) errors.Add($"{prefix} has an invalid name.");
            else if (!componentNames.Add(component.Name)) errors.Add($"component name '{component.Name}' is duplicated.");
            if (component.Schema is < 1 or > ushort.MaxValue) errors.Add($"{prefix} has an invalid schema version.");
            if (!Enum.IsDefined(component.Mode)) errors.Add($"{prefix} has an invalid replication mode.");
            if (component.Fields is null || component.ReservedFieldIds is null) { errors.Add($"{prefix} requires fields and reservedFieldIds arrays."); continue; }
            if (component.Fields.Count > MaximumFieldsPerComponent) errors.Add($"{prefix} exceeds the limit of {MaximumFieldsPerComponent} fields.");
            var fieldIds = new HashSet<int>();
            var fieldNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (ReplicationManifestField field in component.Fields)
            {
                if (field is null) { errors.Add($"{prefix} fields cannot contain null."); continue; }
                if (field.Id is < 1 or > byte.MaxValue) errors.Add($"{prefix} field {field.Id} has an invalid ID.");
                else if (!fieldIds.Add(field.Id)) errors.Add($"{prefix} field ID {field.Id} is duplicated.");
                if (!ValidName(field.Name)) errors.Add($"{prefix} field {field.Id} has an invalid name.");
                else if (!fieldNames.Add(field.Name)) errors.Add($"{prefix} field name '{field.Name}' is duplicated.");
                if (!ValidName(field.WireType)) errors.Add($"{prefix} field {field.Id} has an invalid wire type.");
                if (!float.IsFinite(field.Quantize) || field.Quantize < 0 || !float.IsFinite(field.Threshold) || field.Threshold < 0)
                    errors.Add($"{prefix} field {field.Id} has invalid quantization or threshold.");
                if (!Enum.IsDefined(field.Interpolation)) errors.Add($"{prefix} field {field.Id} has an invalid interpolation mode.");
            }
            ValidateReserved(component.ReservedFieldIds, fieldIds, byte.MaxValue, $"{prefix} reserved field", errors);
        }
        ValidateReserved(manifest.ReservedComponentIds, componentIds, ushort.MaxValue, "reserved component", errors);
        return new(errors);
    }

    /// <summary>
    /// Checks a candidate against its checked-in predecessor. Removed identities must be reserved;
    /// names never move between IDs; and changing a field wire type requires a schema increment.
    /// </summary>
    public static ReplicationManifestValidationResult ValidateEvolution(ReplicationManifest current, ReplicationManifest candidate)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);
        var errors = new List<string>();
        errors.AddRange(Validate(current).Errors.Select(static error => "current: " + error));
        errors.AddRange(Validate(candidate).Errors.Select(static error => "candidate: " + error));
        if (errors.Count != 0) return new(errors);

        var candidateById = candidate.Components.ToDictionary(static component => component.Id);
        var candidateByName = candidate.Components.ToDictionary(static component => component.Name, StringComparer.Ordinal);
        foreach (ReplicationManifestComponent oldComponent in current.Components)
        {
            if (!candidateById.TryGetValue(oldComponent.Id, out ReplicationManifestComponent? nextComponent))
            {
                if (!candidate.ReservedComponentIds.Contains(oldComponent.Id)) errors.Add($"component {oldComponent.Id} ('{oldComponent.Name}') was deleted without reservation.");
                continue;
            }
            if (!StringComparer.Ordinal.Equals(oldComponent.Name, nextComponent.Name)) errors.Add($"component ID {oldComponent.Id} was reused from '{oldComponent.Name}' to '{nextComponent.Name}'.");
            if (nextComponent.Schema < oldComponent.Schema) errors.Add($"component {oldComponent.Id} schema regressed from {oldComponent.Schema} to {nextComponent.Schema}.");
            CompareFields(oldComponent, nextComponent, errors);
        }
        foreach (int reserved in current.ReservedComponentIds)
            if (!candidate.ReservedComponentIds.Contains(reserved)) errors.Add($"reserved component ID {reserved} was removed.");
        foreach (ReplicationManifestComponent nextComponent in candidate.Components)
        {
            if (current.ReservedComponentIds.Contains(nextComponent.Id)) errors.Add($"reserved component ID {nextComponent.Id} was reused by '{nextComponent.Name}'.");
            if (candidateByName.TryGetValue(nextComponent.Name, out ReplicationManifestComponent? named) && named.Id != nextComponent.Id)
                errors.Add($"component name '{nextComponent.Name}' moved from ID {named.Id} to {nextComponent.Id}.");
        }
        return new(errors);
    }

    /// <summary>Builds a manifest-shaped view from generated descriptor metadata in loaded assemblies.</summary>
    public static ReplicationManifest FromGeneratedDescriptors(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var components = new List<ReplicationManifestComponent>();
        foreach (Assembly assembly in assemblies.Where(static assembly => assembly is not null).Distinct())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException exception) { throw new InvalidOperationException($"Could not inspect generated replication descriptors in '{assembly.FullName}'.", exception); }
            foreach (Type type in types)
            {
                PropertyInfo? property = type.GetProperty("GeneratedReplicationDescriptor", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (property?.PropertyType != typeof(ReplicationComponentDescriptor) || property.GetMethod is null) continue;
                if (property.GetValue(null) is not ReplicationComponentDescriptor descriptor) continue;
                components.Add(new ReplicationManifestComponent
                {
                    Id = descriptor.ComponentId,
                    Name = descriptor.ComponentType,
                    Schema = descriptor.SchemaVersion,
                    Mode = descriptor.Mode,
                    Required = descriptor.Required,
                    Fields = descriptor.Fields.Select(static field => new ReplicationManifestField
                    {
                        Id = field.FieldId, Name = field.Name, WireType = field.WireType, Quantize = field.Quantize,
                        Threshold = field.Threshold, Interpolation = field.Interpolation, Optional = field.Optional
                    }).OrderBy(static field => field.Id).ToList()
                });
            }
        }
        return new ReplicationManifest { ReplicationProtocol = CurrentProtocol, Components = components.OrderBy(static component => component.Id).ToList() };
    }

    /// <summary>Checks that checked-in metadata exactly describes the generated descriptors.</summary>
    public static ReplicationManifestValidationResult VerifyGeneratedDescriptors(ReplicationManifest manifest, IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ReplicationManifest generated = FromGeneratedDescriptors(assemblies);
        generated = new ReplicationManifest { ReplicationProtocol = generated.ReplicationProtocol, Components = generated.Components, ReservedComponentIds = manifest.ReservedComponentIds.ToList() };
        var errors = new List<string>();
        errors.AddRange(ValidateEvolution(manifest, generated).Errors);
        var declared = manifest.Components.ToDictionary(static component => component.Id);
        var emitted = generated.Components.ToDictionary(static component => component.Id);
        foreach ((int id, ReplicationManifestComponent component) in declared)
        {
            if (!emitted.TryGetValue(id, out ReplicationManifestComponent? descriptor)) continue;
            if (!Equivalent(component, descriptor)) errors.Add($"component {id} metadata differs from its generated descriptor.");
        }
        foreach (ReplicationManifestComponent component in generated.Components)
            if (!declared.ContainsKey(component.Id)) errors.Add($"generated component {component.Id} ('{component.Name}') is missing from the manifest.");
        return new(errors);
    }

    private static void CompareFields(ReplicationManifestComponent oldComponent, ReplicationManifestComponent nextComponent, List<string> errors)
    {
        var nextFields = nextComponent.Fields.ToDictionary(static field => field.Id);
        foreach (ReplicationManifestField oldField in oldComponent.Fields)
        {
            if (!nextFields.TryGetValue(oldField.Id, out ReplicationManifestField? nextField))
            {
                if (!nextComponent.ReservedFieldIds.Contains(oldField.Id)) errors.Add($"component {oldComponent.Id} field {oldField.Id} ('{oldField.Name}') was deleted without reservation.");
                continue;
            }
            if (!StringComparer.Ordinal.Equals(oldField.Name, nextField.Name)) errors.Add($"component {oldComponent.Id} field ID {oldField.Id} was reused from '{oldField.Name}' to '{nextField.Name}'.");
            if (!StringComparer.Ordinal.Equals(oldField.WireType, nextField.WireType) && nextComponent.Schema <= oldComponent.Schema)
                errors.Add($"component {oldComponent.Id} field {oldField.Id} changed wire type from '{oldField.WireType}' to '{nextField.WireType}' without a schema increment.");
        }
        foreach (int reserved in oldComponent.ReservedFieldIds)
            if (!nextComponent.ReservedFieldIds.Contains(reserved)) errors.Add($"component {oldComponent.Id} reserved field ID {reserved} was removed.");
        foreach (ReplicationManifestField nextField in nextComponent.Fields)
        {
            if (oldComponent.ReservedFieldIds.Contains(nextField.Id)) errors.Add($"component {oldComponent.Id} reserved field ID {nextField.Id} was reused by '{nextField.Name}'.");
            if (!oldComponent.Fields.Any(field => field.Id == nextField.Id) && !nextField.Optional && nextComponent.Schema <= oldComponent.Schema)
                errors.Add($"component {oldComponent.Id} added required field {nextField.Id} without a schema increment.");
        }
    }

    private static bool Equivalent(ReplicationManifestComponent left, ReplicationManifestComponent right) =>
        left.Id == right.Id && left.Schema == right.Schema && left.Mode == right.Mode && left.Required == right.Required &&
        StringComparer.Ordinal.Equals(left.Name, right.Name) && left.Fields.Count == right.Fields.Count &&
        left.Fields.OrderBy(static field => field.Id).Zip(right.Fields.OrderBy(static field => field.Id)).All(static pair =>
            pair.First.Id == pair.Second.Id && pair.First.Optional == pair.Second.Optional && pair.First.Interpolation == pair.Second.Interpolation &&
            StringComparer.Ordinal.Equals(pair.First.Name, pair.Second.Name) && StringComparer.Ordinal.Equals(pair.First.WireType, pair.Second.WireType) &&
            BitConverter.SingleToInt32Bits(pair.First.Quantize) == BitConverter.SingleToInt32Bits(pair.Second.Quantize) &&
            BitConverter.SingleToInt32Bits(pair.First.Threshold) == BitConverter.SingleToInt32Bits(pair.Second.Threshold));

    private static void ValidateReserved(IEnumerable<int> reserved, ISet<int> active, int maximum, string label, List<string> errors)
    {
        var ids = new HashSet<int>();
        foreach (int id in reserved)
        {
            if (id < 1 || id > maximum) { errors.Add($"{label} ID {id} is invalid."); continue; }
            if (!ids.Add(id)) errors.Add($"{label} ID {id} is duplicated.");
            if (active.Contains(id)) errors.Add($"{label} ID {id} is also active.");
        }
    }

    private static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumNameLength;
}
