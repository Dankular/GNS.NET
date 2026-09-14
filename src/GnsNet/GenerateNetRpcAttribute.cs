namespace GnsNet;

/// <summary>Marks a partial message for deterministic generated wire-ID metadata.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateNetMessageAttribute : Attribute
{
    public GenerateNetMessageAttribute(string? name = null) => this.Name = name;
    public string? Name { get; }
}

/// <summary>Marks a partial RPC contract for generated endpoint metadata.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateNetRpcAttribute : Attribute
{
    public GenerateNetRpcAttribute(string endpoint, RpcAuthority authority = RpcAuthority.AnyAuthenticated) { this.Endpoint = endpoint; this.Authority = authority; }
    public string Endpoint { get; }
    public RpcAuthority Authority { get; }
}
