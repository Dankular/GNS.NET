namespace GnsNet;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateNetSchemaAttribute : Attribute
{
    public GenerateNetSchemaAttribute(int version = 1) => this.Version = version;
    public int Version { get; }
}
