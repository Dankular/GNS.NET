namespace GnsNet.Poc;

/// <summary>
/// A deterministic movement pattern used in place of real input - matching the original POC's
/// "client sending scripted directional input". There's no keyboard/controller reading here.
/// </summary>
public static class ScriptedInput
{
    private const uint PhaseLengthTicks = 40; // 2 seconds at the 20 Hz tick rate

    /// <summary>The (dx, dy) input to send for the given client tick.</summary>
    public static (sbyte Dx, sbyte Dy) At(uint tick)
    {
        uint phase = (tick / PhaseLengthTicks) % 4;
        return phase switch
        {
            0 => ((sbyte)1, (sbyte)0),  // right
            1 => ((sbyte)0, (sbyte)1),  // down
            2 => ((sbyte)-1, (sbyte)0), // left
            _ => ((sbyte)0, (sbyte)-1), // up
        };
    }
}
