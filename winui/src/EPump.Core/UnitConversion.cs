namespace EPump.Core;

/// <summary>Fault geometry used by the shear stress conversion, as in the NI6451 application.</summary>
public enum FaultType
{
    OneD,
    TwoD,
}

public static class FaultTypeExtensions
{
    public static string ToLabel(this FaultType t) => t == FaultType.OneD ? "1D" : "2D";

    public static FaultType Parse(string label) => label switch
    {
        "1D" => FaultType.OneD,
        "2D" => FaultType.TwoD,
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "Fault type must be \"1D\" or \"2D\"."),
    };
}

/// <summary>
/// Pump pressure → shear stress on the fault. The piston and fault geometry are those of
/// NI6451's <c>UnitConversion.GetShearStress</c>, so for the same oil pressure both
/// applications show the same shear stress. The only difference is the input: NI6451 gets
/// the oil pressure from a transducer voltage, while here it is the pump's own reading.
/// </summary>
public static class UnitConversion
{
    private const double Mm = 1e-3;
    private const double FaultLengthM = 500 * Mm;

    /// <summary>Area of one shear piston, m².</summary>
    private const double ShearPistonAreaM2 = 12.67e-3;

    public const double PaPerPsi = 6894.757293168;

    /// <summary>Thicknesses offered for each fault type, in (label, metres) pairs. 2D is fixed at 50 cm.</summary>
    public static readonly (string Label, double Metres)[] Thickness1DOptions = [("5 cm", 0.05), ("10 cm", 0.10)];

    public static readonly (string Label, double Metres)[] Thickness2DOptions = [("50 cm (fixed)", 0.50)];

    public static (string Label, double Metres)[] ThicknessOptions(FaultType t) =>
        t == FaultType.OneD ? Thickness1DOptions : Thickness2DOptions;

    /// <summary>
    /// Shear stress in Pa. 1D: one piston; 2D: three pistons and the thickness fixed at 50 cm,
    /// so <paramref name="faultThicknessM"/> is ignored for 2D.
    /// </summary>
    public static double GetShearStress(double pressurePsi, FaultType faultType, double faultThicknessM)
    {
        double pistonArea;
        if (faultType == FaultType.OneD)
        {
            pistonArea = ShearPistonAreaM2 * 1;
        }
        else
        {
            pistonArea = ShearPistonAreaM2 * 3;
            faultThicknessM = 500 * Mm;
        }

        double faultArea = faultThicknessM * FaultLengthM;
        return pressurePsi * PaPerPsi * pistonArea / faultArea;
    }
}

/// <summary>The fault geometry chosen for a run; fixed for the length of a recording.</summary>
public readonly record struct FaultGeometry(FaultType Type, double ThicknessM)
{
    /// <summary>The effective thickness: 2D is always 50 cm.</summary>
    public double EffectiveThicknessM => Type == FaultType.TwoD ? 0.5 : ThicknessM;

    public double ShearStressMPa(double pressurePsi) => UnitConversion.GetShearStress(pressurePsi, Type, ThicknessM) / 1e6;
}
