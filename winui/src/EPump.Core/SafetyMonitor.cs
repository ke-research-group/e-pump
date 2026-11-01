namespace EPump.Core;

/// <summary>One poll cycle's worth of pump state. Pressures in psi, flows in mL/min.</summary>
/// <param name="TimeUnix">Wall-clock time of the <c>CC</c> reply, Unix seconds (UTC). Used to line this recording up with NI6451 data.</param>
/// <param name="PressureLimitPsi">The upper pressure limit the pump itself reports holding, not the one the UI asked for.</param>
public sealed record PumpSample(
    double TimeUnix,
    double PressurePsi,
    double Flow,
    double FlowSetPoint,
    double PressureLimitPsi,
    bool Running,
    PumpFaults Faults,
    bool Leak);

public enum TripReason { None, OverPressure, FlowDeviation, PumpFault }

public readonly record struct SafetyVerdict(TripReason Reason, string Message)
{
    public static readonly SafetyVerdict Ok = new(TripReason.None, string.Empty);

    public bool Tripped => Reason != TripReason.None;
}

/// <summary>
/// The software half of the constant-flow safety rules. The pump's own upper pressure limit
/// is the first line of defence; this is the second, and it also enforces the rule the pump
/// cannot: in constant-flow use, a pump that starts throttling its flow must be stopped, not
/// left to hold pressure.
///
/// Pure logic with no I/O, so every rule is exercised by the self-test.
/// </summary>
public sealed class SafetyMonitor
{
    private double _graceUntilUnix = double.NegativeInfinity;
    private int _lowFlowRun;
    private bool _faultLatched;
    private double _pressureLimitPsi;

    /// <param name="pressureLimitPsi">The operator's limit; anything above <see cref="PumpConfig.PressureCapPsi"/> is rejected.</param>
    public SafetyMonitor(double pressureLimitPsi) => PressureLimitPsi = pressureLimitPsi;

    public double PressureLimitPsi
    {
        get => _pressureLimitPsi;
        set => _pressureLimitPsi = ValidateLimit(value);
    }

    /// <summary>Throws unless <paramref name="psi"/> is a usable upper limit: positive and at most the cap.</summary>
    public static double ValidateLimit(double psi) =>
        double.IsFinite(psi) && psi > 0 && psi <= PumpConfig.PressureCapPsi
            ? psi
            : throw new ArgumentOutOfRangeException(nameof(psi), psi,
                $"The pressure limit must be above 0 and at most {PumpConfig.PressureCapPsi:0} psi.");

    /// <summary>Suspend the flow check while the pump ramps: call on Run and on every flow change.</summary>
    public void ArmGrace(double nowUnix)
    {
        _graceUntilUnix = nowUnix + PumpConfig.FlowCheckGraceSeconds;
        _lowFlowRun = 0;
    }

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

        if (!s.Running)
        {
            _lowFlowRun = 0;
            return SafetyVerdict.Ok;
        }

        if (s.PressurePsi >= PressureLimitPsi)
        {
            return new SafetyVerdict(TripReason.OverPressure,
                $"Pressure reached {s.PressurePsi:F0} psi, at or above the {PressureLimitPsi:F0} psi limit.");
        }

        if (s.TimeUnix < _graceUntilUnix || s.FlowSetPoint <= 0)
        {
            _lowFlowRun = 0;
            return SafetyVerdict.Ok;
        }

        if (s.Flow < s.FlowSetPoint * (1 - PumpConfig.FlowDeviationTolerance))
        {
            if (++_lowFlowRun >= PumpConfig.FlowDeviationSamples)
            {
                _lowFlowRun = 0;
                return new SafetyVerdict(TripReason.FlowDeviation,
                    $"Flow fell to {s.Flow:0.000} mL/min against a {s.FlowSetPoint:0.000} mL/min set point "
                    + $"(more than {PumpConfig.FlowDeviationTolerance:P0} low) at {s.PressurePsi:F0} psi: "
                    + "the pump was throttling to hold pressure, so the flow was no longer constant.");
            }
        }
        else
        {
            _lowFlowRun = 0;
        }

        return SafetyVerdict.Ok;
    }

    public static string DescribeFaults(PumpFaults f)
    {
        var parts = new List<string>(3);
        if (f.OverPressure) parts.Add("upper pressure limit exceeded");
        if (f.UnderPressure) parts.Add("pressure below the lower limit");
        if (f.MotorStall) parts.Add("motor stall");
        return "The pump stopped itself: " + string.Join(", ", parts) + ".";
    }
}
