namespace GnsNet.Generators;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

/// <summary>Semantic incremental generator for explicit-ID replicated components.</summary>
[Generator]
public sealed class ReplicationGenerator : IIncrementalGenerator
{
    private const string ComponentAttribute = "GnsNet.Replication.ReplicatedComponentAttribute";
    private const string FieldAttribute = "GnsNet.Replication.ReplicatedFieldAttribute";
    private const string IgnoreAttribute = "GnsNet.Replication.ReplicatedIgnoreAttribute";

    private static readonly DiagnosticDescriptor MustBePartial = Error("GNS100", "Replicated component must be partial", "Replicated component '{0}' must be declared partial.");
    private static readonly DiagnosticDescriptor InvalidComponentId = Error("GNS101", "Missing or invalid component ID", "Replicated component '{0}' must use a non-zero component ID and schema version.");
    private static readonly DiagnosticDescriptor DuplicateComponentId = Error("GNS102", "Duplicate component ID", "Component ID {0} is used by both '{1}' and '{2}'.");
    private static readonly DiagnosticDescriptor InvalidFieldId = Error("GNS103", "Missing or invalid field ID", "Replicated member '{0}' must use a non-zero field ID; components must declare at least one replicated member.");
    private static readonly DiagnosticDescriptor DuplicateFieldId = Error("GNS104", "Duplicate field ID", "Field ID {0} is used more than once in component '{1}'.");
    private static readonly DiagnosticDescriptor UnsupportedFieldType = Error("GNS105", "Unsupported replicated field type", "Replicated member '{0}' has unsupported type '{1}'. Use a fixed primitive, enum, or System.Numerics vector/quaternion.");
    private static readonly DiagnosticDescriptor UnreadableOrUnwritable = Error("GNS106", "Field cannot be read or applied", "Replicated member '{0}' must be an instance field that is not readonly or a property with an ordinary getter and setter.");
    private static readonly DiagnosticDescriptor InvalidQuantization = Error("GNS107", "Invalid quantization or threshold", "Replicated member '{0}' has invalid Quantize or Threshold settings. They must be finite, non-negative, and only apply to floating-point or numeric-vector fields.");
    private static readonly DiagnosticDescriptor InvalidInterpolation = Error("GNS108", "Interpolation mode incompatible with field type", "Interpolation mode '{0}' is not supported for replicated member '{1}' of type '{2}'.");
    private static readonly DiagnosticDescriptor UnboundedField = Error("GNS109", "Unbounded variable-length field", "Replicated member '{0}' is variable length. Strings and byte arrays require an explicit maximum, which is not available in the fixed-size v1 codec.");
    private static readonly DiagnosticDescriptor UnsafeDeclaration = Error("GNS112", "Nested or generic declaration cannot be emitted safely", "Replicated component '{0}' and every containing type must be non-generic partial declarations.");

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ComponentModel> components = context.SyntaxProvider.ForAttributeWithMetadataName(
            ComponentAttribute,
            static (node, _) => node is TypeDeclarationSyntax,
            static (attributeContext, _) => BuildModel(attributeContext))
            .Where(static model => model is not null)
            .Select(static (model, _) => model!.Value);

        context.RegisterSourceOutput(components.Collect(), static (production, models) => Emit(production, models));
    }

    private static ComponentModel? BuildModel(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol type || context.Attributes.Length == 0) return null;
        AttributeData attribute = context.Attributes[0];
        var diagnostics = ImmutableArray.CreateBuilder<DiagnosticInfo>();
        Location typeLocation = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? type.Locations.FirstOrDefault() ?? Location.None;
        ushort componentId = GetUShort(attribute.ConstructorArguments, 0);
        ushort schemaVersion = GetNamedUShort(attribute, "SchemaVersion", 1);
        byte mode = GetNamedByte(attribute, "Mode", 0);
        bool required = GetNamedBool(attribute, "Required", true);

        if (componentId == 0 || schemaVersion == 0) diagnostics.Add(new(InvalidComponentId, typeLocation, type.Name));
        if (!IsPartial(type)) diagnostics.Add(new(MustBePartial, typeLocation, type.Name));
        if (!CanEmitContainingTypes(type)) diagnostics.Add(new(UnsafeDeclaration, typeLocation, type.ToDisplayString()));
        if (type.TypeParameters.Length != 0) diagnostics.Add(new(UnsafeDeclaration, typeLocation, type.ToDisplayString()));
        if (type.TypeKind == TypeKind.Class && !type.InstanceConstructors.Any(ctor => ctor.Parameters.Length == 0)) diagnostics.Add(new(UnreadableOrUnwritable, typeLocation, type.Name));

        var fields = ImmutableArray.CreateBuilder<FieldModel>();
        foreach (ISymbol member in type.GetMembers())
        {
            if (member.GetAttributes().Any(static candidate => candidate.AttributeClass?.ToDisplayString() == IgnoreAttribute)) continue;
            AttributeData? fieldAttribute = member.GetAttributes().FirstOrDefault(static candidate => candidate.AttributeClass?.ToDisplayString() == FieldAttribute);
            if (fieldAttribute is null) continue;

            Location location = fieldAttribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? member.Locations.FirstOrDefault() ?? typeLocation;
            byte fieldId = GetByte(fieldAttribute.ConstructorArguments, 0);
            float quantize = GetNamedFloat(fieldAttribute, "Quantize", 0);
            float threshold = GetNamedFloat(fieldAttribute, "Threshold", 0);
            byte interpolation = GetNamedByte(fieldAttribute, "Interpolation", 0);
            bool optional = GetNamedBool(fieldAttribute, "Optional", false);
            ITypeSymbol? memberType = member switch { IFieldSymbol field => field.Type, IPropertySymbol property => property.Type, _ => null };

            if (fieldId == 0) diagnostics.Add(new(InvalidFieldId, location, member.Name));
            if (!CanReadAndWrite(member)) diagnostics.Add(new(UnreadableOrUnwritable, location, member.Name));
            if (memberType is null || !TryGetWireType(memberType, out WireType wireType))
            {
                diagnostics.Add(new(IsVariableLength(memberType) ? UnboundedField : UnsupportedFieldType, location, member.Name, memberType?.ToDisplayString() ?? "unknown"));
                continue;
            }
            if ((float.IsNaN(quantize) || float.IsInfinity(quantize) || float.IsNaN(threshold) || float.IsInfinity(threshold) || quantize < 0 || threshold < 0 || ((quantize > 0 || threshold > 0) && !wireType.IsFloating)))
                diagnostics.Add(new(InvalidQuantization, location, member.Name));
            if (!InterpolationIsSupported(interpolation, wireType)) diagnostics.Add(new(InvalidInterpolation, location, interpolation, member.Name, memberType.ToDisplayString()));

            fields.Add(new FieldModel(fieldId, member.Name, memberType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), wireType, quantize, threshold, interpolation, optional, location));
        }

        if (fields.Count == 0) diagnostics.Add(new(InvalidFieldId, typeLocation, type.Name));
        foreach (IGrouping<byte, FieldModel> group in fields.GroupBy(static field => field.Id).Where(static group => group.Key != 0 && group.Count() > 1))
            foreach (FieldModel field in group) diagnostics.Add(new(DuplicateFieldId, field.Location, group.Key, type.Name));

        return new ComponentModel(
            componentId,
            schemaVersion,
            mode,
            required,
            type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            GetNamespace(type),
            GetTypePath(type),
            fields.OrderBy(static field => field.Id).ToImmutableArray(),
            diagnostics.ToImmutable());
    }

    private static void Emit(SourceProductionContext production, ImmutableArray<ComponentModel> models)
    {
        foreach (ComponentModel model in models)
            foreach (DiagnosticInfo diagnostic in model.Diagnostics)
                production.ReportDiagnostic(Diagnostic.Create(diagnostic.Descriptor, diagnostic.Location, diagnostic.Arguments));

        foreach (IGrouping<ushort, ComponentModel> group in models.Where(static model => model.ComponentId != 0).GroupBy(static model => model.ComponentId).Where(static group => group.Count() > 1))
        {
            ComponentModel first = group.First();
            foreach (ComponentModel duplicate in group.Skip(1))
                production.ReportDiagnostic(Diagnostic.Create(DuplicateComponentId, Location.None, group.Key, first.QualifiedName, duplicate.QualifiedName));
        }

        foreach (ComponentModel model in models)
        {
            if (model.Diagnostics.Length != 0 || model.ComponentId == 0 || model.SchemaVersion == 0 || model.Fields.Length == 0 || models.Count(other => other.ComponentId == model.ComponentId) != 1) continue;
            string source = Generate(model);
            production.AddSource(SanitizeHintName(model.QualifiedName) + ".Replication.g.cs", SourceText.From(source, Encoding.UTF8));
        }
    }

    private static string Generate(ComponentModel model)
    {
        var source = new StringBuilder("// <auto-generated />\n#nullable enable\n");
        if (model.Namespace.Length > 0) source.Append("namespace ").Append(model.Namespace).Append("\n{\n");
        foreach (string type in model.TypePath) source.Append("partial ").Append(type).Append("\n{\n");
        source.Append("public static global::GnsNet.Replication.ReplicationComponentDescriptor GeneratedReplicationDescriptor { get; } = new global::GnsNet.Replication.ReplicationComponentDescriptor(")
            .Append(model.ComponentId).Append(", ").Append(model.SchemaVersion).Append(", (global::GnsNet.Replication.ReplicationMode)").Append(model.Mode).Append(", ").Append(model.Required ? "true" : "false").Append(", \"").Append(model.QualifiedName.Replace("global::", string.Empty)).Append("\", new global::GnsNet.Replication.ReplicationFieldDescriptor[] { ");
        source.Append(string.Join(", ", model.Fields.Select(FieldDescriptor)));
        source.Append(" });\n");
        source.Append("public sealed class ReplicationCodec : global::GnsNet.Replication.IReplicatedComponentCodec<").Append(model.QualifiedName).Append(">\n{\n")
            .Append("public static ushort ComponentId => ").Append(model.ComponentId).Append(";\n")
            .Append("public static ushort SchemaVersion => ").Append(model.SchemaVersion).Append(";\n");
        EmitDiff(source, model);
        EmitWrite(source, model, false);
        EmitWrite(source, model, true);
        EmitRead(source, model, false);
        EmitRead(source, model, true);
        EmitInterpolate(source, model);
        source.Append("}\n");
        for (int index = model.TypePath.Length - 1; index >= 0; index--) source.Append("}\n");
        if (model.Namespace.Length > 0) source.Append("}\n");
        return source.ToString();
    }

    private static string FieldDescriptor(FieldModel field) => "new global::GnsNet.Replication.ReplicationFieldDescriptor(" + field.Id + ", \"" + field.Name + "\", \"" + field.Wire.Name + "\", " + FloatLiteral(field.Quantize) + ", " + FloatLiteral(field.Threshold) + ", (global::GnsNet.Replication.InterpolationMode)" + field.Interpolation + ", " + (field.Optional ? "true" : "false") + ")";

    private static void EmitDiff(StringBuilder source, ComponentModel model)
    {
        source.Append("public static global::GnsNet.DirtyFieldMask Diff(in ").Append(model.QualifiedName).Append(" baseline, in ").Append(model.QualifiedName).Append(" current)\n{\nvar mask = new global::GnsNet.DirtyFieldMask(").Append(model.Fields.Length).Append(");\n");
        for (int index = 0; index < model.Fields.Length; index++)
        {
            FieldModel field = model.Fields[index];
            source.Append("if (!(").Append(EqualityExpression(field, "baseline." + field.Name, "current." + field.Name)).Append(")) mask.Set(").Append(index).Append(");\n");
        }
        source.Append("return mask;\n}\n");
    }

    private static void EmitWrite(StringBuilder source, ComponentModel model, bool delta)
    {
        source.Append("public static void Write").Append(delta ? "Delta" : "Full").Append("(ref global::GnsNet.Replication.ReplicationWriter writer, ");
        if (delta) source.Append("in ").Append(model.QualifiedName).Append(" baseline, in ").Append(model.QualifiedName).Append(" current, in global::GnsNet.DirtyFieldMask mask");
        else source.Append("in ").Append(model.QualifiedName).Append(" value");
        source.Append(")\n{\n");
        if (delta)
        {
            source.Append("if (mask is null || mask.FieldCount != ").Append(model.Fields.Length).Append(") throw new global::System.ArgumentException(\"Invalid replication dirty mask.\", nameof(mask));\n");
            source.Append("foreach (ulong word in mask.Words) writer.WriteUInt64(word);\n");
        }
        for (int index = 0; index < model.Fields.Length; index++)
        {
            FieldModel field = model.Fields[index];
            if (delta) source.Append("if (mask.IsSet(").Append(index).Append(")) ");
            source.Append(WriteStatement(field, (delta ? "current." : "value.") + field.Name)).Append("\n");
        }
        source.Append("}\n");
    }

    private static void EmitRead(StringBuilder source, ComponentModel model, bool delta)
    {
        source.Append("public static bool Try").Append(delta ? "ApplyDelta" : "ReadFull").Append("(ref global::GnsNet.Replication.ReplicationReader reader, ");
        if (delta) source.Append("in ").Append(model.QualifiedName).Append(" baseline, ");
        source.Append("out ").Append(model.QualifiedName).Append(" value)\n{\n");
        source.Append("var temporary = ").Append(CreateInstance(model)).Append(";\n");
        if (delta)
        {
            foreach (FieldModel field in model.Fields) source.Append("temporary.").Append(field.Name).Append(" = baseline.").Append(field.Name).Append(";\n");
            source.Append("ulong[] words = new ulong[").Append((model.Fields.Length + 63) / 64).Append("];\n");
            source.Append("for (int word = 0; word < words.Length; word++) if (!reader.TryReadUInt64(out words[word])) { value = default!; return false; }\n");
            int lastBits = model.Fields.Length % 64;
            if (lastBits != 0) source.Append("if ((words[^1] & ~((1UL << ").Append(lastBits).Append(") - 1)) != 0) { value = default!; return false; }\n");
        }
        for (int index = 0; index < model.Fields.Length; index++)
        {
            FieldModel field = model.Fields[index];
            if (delta) source.Append("if ((words[").Append(index / 64).Append("] & (1UL << ").Append(index % 64).Append(")) != 0) { ");
            source.Append(ReadStatement(field, "temporary." + field.Name)).Append("\n");
            if (delta) source.Append("}\n");
        }
        source.Append("if (reader.Remaining != 0) { value = default!; return false; }\nvalue = temporary; return true;\n}\n");
    }

    private static void EmitInterpolate(StringBuilder source, ComponentModel model)
    {
        source.Append("public static ").Append(model.QualifiedName).Append(" Interpolate(in ").Append(model.QualifiedName).Append(" from, in ").Append(model.QualifiedName).Append(" to, float alpha)\n{\nalpha = global::System.Math.Clamp(alpha, 0f, 1f); var result = ").Append(CreateInstance(model)).Append(";\n");
        foreach (FieldModel field in model.Fields)
            source.Append("result.").Append(field.Name).Append(" = ").Append(InterpolationExpression(field, "from." + field.Name, "to." + field.Name)).Append(";\n");
        source.Append("return result;\n}\n");
    }

    private static string CreateInstance(ComponentModel model) => model.IsClass ? "new " + model.QualifiedName + "()" : "default(" + model.QualifiedName + ")";
    private static string EqualityExpression(FieldModel field, string left, string right) => field.Wire.IsFloating ? "global::GnsNet.Replication.ReplicationMath.Equal(" + left + ", " + right + ", " + FloatLiteral(field.Threshold) + ", " + FloatLiteral(field.Quantize) + ")" : left + " == " + right;
    private static string WriteStatement(FieldModel field, string value)
    {
        string encoded = field.Wire.IsFloating && field.Quantize > 0 ? "global::GnsNet.Replication.ReplicationMath.Quantize(" + value + ", " + FloatLiteral(field.Quantize) + ")" : value;
        return "writer." + field.Wire.WriteMethod + "(" + (field.Wire.EnumUnderlyingType is null ? encoded : "(" + field.Wire.EnumUnderlyingType + ")" + encoded) + ");";
    }
    private static string ReadStatement(FieldModel field, string destination)
    {
        string variable = "field" + field.Id;
        string type = field.Wire.EnumUnderlyingType ?? field.TypeName;
        string assign = field.Wire.EnumUnderlyingType is null ? variable : "(" + field.TypeName + ")" + variable;
        return "if (!reader." + field.Wire.ReadMethod + "(out " + type + " " + variable + ")) { value = default!; return false; } " + destination + " = " + assign + ";";
    }
    private static string InterpolationExpression(FieldModel field, string from, string to)
    {
        if (field.Interpolation == 1 || field.Interpolation == 4 || !field.Wire.IsFloating) return "alpha < 0.5f ? " + from + " : " + to;
        if (field.Wire.Name == "quaternion") return "global::System.Numerics.Quaternion.Slerp(" + from + ", " + to + ", alpha)";
        return field.Wire.Name switch
        {
            "float32" => from + " + (" + to + " - " + from + ") * alpha",
            "float64" => from + " + (" + to + " - " + from + ") * alpha",
            "vector2" or "vector3" or "vector4" => "global::System.Numerics." + field.Wire.VectorType + ".Lerp(" + from + ", " + to + ", alpha)",
            _ => "alpha < 0.5f ? " + from + " : " + to
        };
    }

    private static bool CanReadAndWrite(ISymbol member) => member switch
    {
        IFieldSymbol field => !field.IsStatic && !field.IsReadOnly && !field.IsConst,
        IPropertySymbol property => !property.IsStatic && property.GetMethod is not null && property.SetMethod is not null && !property.SetMethod.IsInitOnly,
        _ => false
    };
    private static bool IsPartial(INamedTypeSymbol type) => type.DeclaringSyntaxReferences.All(reference => reference.GetSyntax() is TypeDeclarationSyntax declaration && declaration.Modifiers.Any(static modifier => modifier.Text == "partial"));
    private static bool CanEmitContainingTypes(INamedTypeSymbol type) => type.ContainingType is null || (type.ContainingType.TypeParameters.Length == 0 && IsPartial(type.ContainingType) && CanEmitContainingTypes(type.ContainingType));
    private static string GetNamespace(INamedTypeSymbol type) => type.ContainingNamespace?.IsGlobalNamespace == false ? type.ContainingNamespace.ToDisplayString() : string.Empty;
    private static ImmutableArray<string> GetTypePath(INamedTypeSymbol type)
    {
        var path = new Stack<string>();
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType) path.Push(TypeDeclaration(current));
        return path.ToImmutableArray();
    }
    private static string TypeDeclaration(INamedTypeSymbol type) => (type.IsRecord ? (type.TypeKind == TypeKind.Struct ? "record struct " : "record ") : type.TypeKind == TypeKind.Struct ? "struct " : "class ") + type.Name;
    private static bool IsVariableLength(ITypeSymbol? type) => type?.SpecialType == SpecialType.System_String || type is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte };
    private static bool InterpolationIsSupported(byte interpolation, WireType type) => interpolation <= 4 && (interpolation != 3 || type.Name == "quaternion") && (interpolation != 2 || type.IsFloating);

    private static bool TryGetWireType(ITypeSymbol type, out WireType wireType)
    {
        if (type.TypeKind == TypeKind.Enum && type is INamedTypeSymbol enumType)
        {
            if (!TryGetWireType(enumType.EnumUnderlyingType!, out wireType)) return false;
            wireType = wireType with { Name = "enum", EnumUnderlyingType = enumType.EnumUnderlyingType!.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) };
            return true;
        }
        wireType = type.SpecialType switch
        {
            SpecialType.System_Boolean => new("bool", "WriteBoolean", "TryReadBoolean", false, ""),
            SpecialType.System_Byte => new("uint8", "WriteByte", "TryReadByte", false, ""),
            SpecialType.System_SByte => new("int8", "WriteSByte", "TryReadSByte", false, ""),
            SpecialType.System_Int16 => new("int16", "WriteInt16", "TryReadInt16", false, ""),
            SpecialType.System_UInt16 or SpecialType.System_Char => new("uint16", "WriteUInt16", "TryReadUInt16", false, ""),
            SpecialType.System_Int32 => new("int32", "WriteInt32", "TryReadInt32", false, ""),
            SpecialType.System_UInt32 => new("uint32", "WriteUInt32", "TryReadUInt32", false, ""),
            SpecialType.System_Int64 => new("int64", "WriteInt64", "TryReadInt64", false, ""),
            SpecialType.System_UInt64 => new("uint64", "WriteUInt64", "TryReadUInt64", false, ""),
            SpecialType.System_Single => new("float32", "WriteSingle", "TryReadSingle", true, ""),
            SpecialType.System_Double => new("float64", "WriteDouble", "TryReadDouble", true, ""),
            _ => default
        };
        if (wireType.Name is not null) return true;
        string name = type.ToDisplayString();
        wireType = name switch
        {
            "System.Numerics.Vector2" => new("vector2", "WriteVector2", "TryReadVector2", true, "Vector2"),
            "System.Numerics.Vector3" => new("vector3", "WriteVector3", "TryReadVector3", true, "Vector3"),
            "System.Numerics.Vector4" => new("vector4", "WriteVector4", "TryReadVector4", true, "Vector4"),
            "System.Numerics.Quaternion" => new("quaternion", "WriteQuaternion", "TryReadQuaternion", true, ""),
            _ => default
        };
        return wireType.Name is not null;
    }

    private static ushort GetUShort(ImmutableArray<TypedConstant> values, int index) => index < values.Length && values[index].Value is ushort value ? value : (ushort)0;
    private static byte GetByte(ImmutableArray<TypedConstant> values, int index) => index < values.Length && values[index].Value is byte value ? value : (byte)0;
    private static ushort GetNamedUShort(AttributeData attribute, string name, ushort fallback) => attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value is ushort value ? value : fallback;
    private static byte GetNamedByte(AttributeData attribute, string name, byte fallback) => attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value is byte value ? value : fallback;
    private static bool GetNamedBool(AttributeData attribute, string name, bool fallback) => attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value is bool value ? value : fallback;
    private static float GetNamedFloat(AttributeData attribute, string name, float fallback) => attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value is float value ? value : fallback;
    private static string FloatLiteral(float value) => value.ToString("R", CultureInfo.InvariantCulture) + "f";
    private static DiagnosticDescriptor Error(string id, string title, string message) => new(id, title, message, "GnsNet.Replication", DiagnosticSeverity.Error, true);
    private static string SanitizeHintName(string name) => new string(name.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());

    private readonly record struct ComponentModel(ushort ComponentId, ushort SchemaVersion, byte Mode, bool Required, string QualifiedName, string Namespace, ImmutableArray<string> TypePath, ImmutableArray<FieldModel> Fields, ImmutableArray<DiagnosticInfo> Diagnostics)
    {
        public bool IsClass => TypePath.Length > 0 && !TypePath[TypePath.Length - 1].StartsWith("struct ", StringComparison.Ordinal) && !TypePath[TypePath.Length - 1].StartsWith("record struct ", StringComparison.Ordinal);
    }
    private readonly record struct FieldModel(byte Id, string Name, string TypeName, WireType Wire, float Quantize, float Threshold, byte Interpolation, bool Optional, Location Location);
    private readonly record struct WireType(string? Name, string WriteMethod, string ReadMethod, bool IsFloating, string VectorType, string? EnumUnderlyingType = null);
    private readonly record struct DiagnosticInfo(DiagnosticDescriptor Descriptor, Location Location, params object[] Arguments);
}
