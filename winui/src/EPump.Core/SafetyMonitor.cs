namespace EPump.Core;

/// <summary>One poll cycle's worth of pump state. Pressures in psi, flows in mL/min.</summary>
/// <param name="TimeUnix">Wall-clock time of the <c>CC</c> reply, Unix seconds (UTC). Used to line this recording up with NI6451 data.</param>
/// <param name="FlowLimit">FI: the most the pump may deliver while holding the target pressure.</param>
/// <param name="PressureSetPointPsi">PS (front panel PRESS): the target pressure the pump reports holding.</param>
/// <param name="PressureLimitPsi">UP (front panel HI PR): the stop limit the pump reports holding.</param>
public sealed record PumpSample(
    double TimeUnix,
    double PressurePsi,
    double Flow,
    double FlowLimit,
    double PressureSetPointPsi,
    double PressureLimitPsi,
    bool Running,
    PumpFaults Faults,
    bool Leak);

public enum TripReason { None, OverPressure, PumpFault }

public readonly record struct SafetyVerdict(TripReason Reason, string Message)
{
    public static readonly SafetyVerdict Ok = new(TripReason.None, string.Empty);

    public bool Tripped => Reason != TripReason.None;
}

/// <summary>
/// The software half of the pressure protection. The pump's own HI PR is the first line of
/// defence; this is the second: a running pump at or above the cap is stopped from here too,
/// whatever its own limit happens to be set to. Pump faults are reported once, when they appear.
///
/// Pure logic with no I/O, so every rule is exercised by the self-test.
/// </summary>
public sealed class SafetyMonitor
{
    private bool _faultLatched;

    /// <summary>Throws unless <paramref name="psi"/> is a usable target pressure: positive and at most the cap.</summary>
    public static double ValidateTarget(double psi) =>
        double.IsFinite(psi) && psi > 0 && psi <= PumpConfig.PressureCapPsi
            ? psi
            : throw new ArgumentOutOfRangeException(nameof(psi), psi,
                $"The target pressure must be above 0 and at most {PumpConfig.PressureCapPsi:0} psi.");

    public SafetyVerdict Evaluate(PumpSample s)
    {
        // A pump fault has already stopped the pump; report it once, on the rising edge.
        if (s.Faults.Any)
        {
            if (_faultLatched) return SafetyVerdict.Ok;
            _faultLatched = true;
            return new SafetyVerdict(TripReason.PumpFault, DescribeFaults(s.Faults));
        }
        _faultLatched = false;

        if (s.Running && s.PressurePsi >= PumpConfig.UpperLimitPsi)
        {
            return new SafetyVerdict(TripReason.OverPressure,
                $"Pressure reached {s.PressurePsi:F0} psi, at or above the {PumpConfig.UpperLimitPsi:F0} psi cap.");
        }

        return SafetyVerdict.Ok;
    }

    public static string DescribeFaults(PumpFaults f)
    {
        var parts = new List<string>(3);
        if (f.OverPressure) parts.Add("upper pressure limit (HI PR) exceeded");
        if (f.UnderPressure) parts.Add("pressure below the lower limit (LO PR)");
        if (f.MotorStall) parts.Add("motor stall");
        return "The pump stopped itself: " + string.Join(", ", parts) + ".";
    }
}
