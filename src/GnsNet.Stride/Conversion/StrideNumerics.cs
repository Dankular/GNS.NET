namespace GnsNet.Stride;

using System.Numerics;

/// <summary>Explicit System.Numerics to Stride math conversions. Both use right-handed XYZW quaternions.</summary>
public static class StrideNumerics
{
    /// <summary>Converts a network vector without changing axes.</summary>
    public static Vector3 ToNumerics(Vector3 value) => value;

    /// <summary>Converts a network vector to Stride coordinates.</summary>
    public static global::Stride.Core.Mathematics.Vector3 ToStride(Vector3 value) => new(value.X, value.Y, value.Z);

    /// <summary>Converts a Stride vector to network coordinates.</summary>
    public static Vector3 ToNumerics(global::Stride.Core.Mathematics.Vector3 value) => new(value.X, value.Y, value.Z);

    /// <summary>Converts a network quaternion preserving XYZW component order.</summary>
    public static global::Stride.Core.Mathematics.Quaternion ToStride(Quaternion value) => new(value.X, value.Y, value.Z, value.W);

    /// <summary>Converts a Stride quaternion preserving XYZW component order.</summary>
    public static Quaternion ToNumerics(global::Stride.Core.Mathematics.Quaternion value) => new(value.X, value.Y, value.Z, value.W);
}
