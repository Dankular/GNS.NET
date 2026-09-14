namespace GnsNet.Tests;

using GnsNet;
using Xunit;

[GenerateNetSchema(2)]
public partial class GeneratedMessageMarker { }

public sealed class GeneratorTests
{
    [Fact]
    public void SourceGenerator_EmitsSchemaVersionForAnnotatedPartialType()
        => Assert.Equal(1, GeneratedMessageMarker.GeneratedNetworkSchemaVersion);
}
