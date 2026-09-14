namespace GnsNet.Generators;

using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

[Generator]
public sealed class NetSchemaGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor MustBePartial = new("GNS001", "Generated network contract requires partial type", "Type '{0}' must be partial when annotated with a GnsNet generator attribute", "GnsNet", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor DuplicateId = new("GNS002", "Duplicate generated network message ID", "Generated ID {0} for '{1}' is already used by another generated message", "GnsNet", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor DuplicateRpc = new("GNS003", "Duplicate generated RPC endpoint", "Generated RPC endpoint '{0}' is declared by more than one contract", "GnsNet", DiagnosticSeverity.Error, true);
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ClassDeclarationSyntax> candidates = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
            static (ctx, _) => (ClassDeclarationSyntax)ctx.Node).Where(static node => node.AttributeLists.Count > 0);
        context.RegisterSourceOutput(candidates.Collect(), static (production, types) =>
        {
            foreach (IGrouping<uint, ClassDeclarationSyntax> group in types.Where(t => t.AttributeLists.SelectMany(x => x.Attributes).Any(a => a.Name.ToString().EndsWith("GenerateNetMessage", StringComparison.Ordinal))).GroupBy(t => Hash(t.Identifier.Text) % 65535 + 1).Where(g => g.Select(t => t.Identifier.Text).Distinct(StringComparer.Ordinal).Count() > 1))
                foreach (ClassDeclarationSyntax type in group) production.ReportDiagnostic(Diagnostic.Create(DuplicateId, type.Identifier.GetLocation(), group.Key, type.Identifier.Text));
            foreach (IGrouping<string, ClassDeclarationSyntax> group in types.Where(t => t.AttributeLists.SelectMany(x => x.Attributes).Any(a => a.Name.ToString().EndsWith("GenerateNetRpc", StringComparison.Ordinal))).GroupBy(RpcEndpoint, StringComparer.Ordinal).Where(g => g.Select(t => t.Identifier.Text).Distinct(StringComparer.Ordinal).Count() > 1))
                foreach (ClassDeclarationSyntax type in group) production.ReportDiagnostic(Diagnostic.Create(DuplicateRpc, type.Identifier.GetLocation(), group.Key));
        });
        context.RegisterSourceOutput(candidates, static (production, type) =>
        {
            string[] attributes = type.AttributeLists.SelectMany(x => x.Attributes).Select(x => x.Name.ToString()).ToArray();
            bool schema = attributes.Any(x => x.EndsWith("GenerateNetSchema", StringComparison.Ordinal));
            bool message = attributes.Any(x => x.EndsWith("GenerateNetMessage", StringComparison.Ordinal));
            bool rpc = attributes.Any(x => x.EndsWith("GenerateNetRpc", StringComparison.Ordinal));
            if (!schema && !message && !rpc) return;
            if (!type.Modifiers.Any(x => x.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PartialKeyword))) { production.ReportDiagnostic(Diagnostic.Create(MustBePartial, type.Identifier.GetLocation(), type.Identifier.Text)); return; }
            string ns = GetNamespace(type); string name = type.Identifier.Text; uint id = Hash(name);
            var members = new StringBuilder();
            if (schema)
            {
                AttributeSyntax schemaAttribute = type.AttributeLists.SelectMany(x => x.Attributes).First(x => x.Name.ToString().EndsWith("GenerateNetSchema", StringComparison.Ordinal));
                string version = schemaAttribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression.ToString() ?? "1";
                members.AppendLine($"    public const int GeneratedNetworkSchemaVersion = {version};");
            }
            if (message)
            {
                members.AppendLine($"    public const ushort GeneratedNetworkMessageId = {id % 65535 + 1};");
                PropertyDeclarationSyntax[] fields = type.Members.OfType<PropertyDeclarationSyntax>().OrderBy(x => x.Identifier.Text, StringComparer.Ordinal).ToArray();
                members.AppendLine($"    public const int GeneratedNetworkFieldCount = {fields.Length};");
                members.AppendLine($"    public static readonly string[] GeneratedNetworkFieldNames = new string[] {{ {string.Join(", ", fields.Select(x => "\"" + x.Identifier.Text + "\""))} }};");
                members.AppendLine($"    public static readonly uint[] GeneratedNetworkFieldIds = new uint[] {{ {string.Join(", ", fields.Select(x => Hash(x.Identifier.Text).ToString() + "u"))} }};");
                string schemaVersion = schema ? "GeneratedNetworkSchemaVersion" : "1";
                members.AppendLine($"    public byte[] EncodeGeneratedFields(global::GnsNet.DirtyFieldMask mask, global::System.Func<int, byte[]> fieldEncoder) => global::GnsNet.DirtyFieldMaskCodec.Encode({schemaVersion}, mask, fieldEncoder);");
                members.AppendLine($"    public static (int SchemaVersion, global::GnsNet.DirtyFieldMask Mask, global::System.Collections.Generic.IReadOnlyDictionary<int, byte[]> Fields) DecodeGeneratedFields(global::System.ReadOnlySpan<byte> data, int maximumFieldBytes = 1048576) => global::GnsNet.DirtyFieldMaskCodec.Decode(data, {schemaVersion}, GeneratedNetworkFieldCount, maximumFieldBytes);");
            }
            if (rpc)
            {
                AttributeSyntax rpcAttribute = type.AttributeLists.SelectMany(x => x.Attributes).First(x => x.Name.ToString().EndsWith("GenerateNetRpc", StringComparison.Ordinal));
                string endpoint = rpcAttribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression.ToString().Trim('"') ?? name;
                string authority = rpcAttribute.ArgumentList?.Arguments.Skip(1).FirstOrDefault()?.Expression.ToString() ?? "RpcAuthority.AnyAuthenticated";
                members.AppendLine($"    public const string GeneratedRpcEndpoint = \"{endpoint}\";");
                members.AppendLine($"    public static void RegisterGeneratedRpc(global::GnsNet.RpcRouter router, global::System.Func<global::GnsNet.RpcRequest, global::GnsNet.RpcResponse> handler) => router.Register(GeneratedRpcEndpoint, global::GnsNet.{authority.Replace("RpcAuthority.", "RpcAuthority.")}, handler);");
            }
            string source = $"// <auto-generated />\nnamespace {ns};\npartial class {name}\n{{\n{members}}}\n";
            production.AddSource($"{name}.GnsSchema.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }
    private static string GetNamespace(ClassDeclarationSyntax type)
    {
        SyntaxNode? parent = type.Parent; while (parent is not null && parent is not BaseNamespaceDeclarationSyntax) parent = parent.Parent;
        return parent switch { BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(), _ => "GnsNet.Generated" };
    }
    private static uint Hash(string value)
    { uint hash = 2166136261; foreach (char c in value) { hash ^= c; hash *= 16777619; } return hash; }
    private static string RpcEndpoint(ClassDeclarationSyntax type)
    { AttributeSyntax? attr = type.AttributeLists.SelectMany(x => x.Attributes).FirstOrDefault(x => x.Name.ToString().EndsWith("GenerateNetRpc", StringComparison.Ordinal)); return attr?.ArgumentList?.Arguments.FirstOrDefault()?.Expression.ToString().Trim('"') ?? type.Identifier.Text; }
}
