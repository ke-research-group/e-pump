using System.Diagnostics;
using System.Globalization;

namespace EPump.Core;

/// <summary>
/// A stand-in for a 12 mL/min ReaXus CP Class pump that speaks the same command set, so the
/// application, the CLI and the self-test can run without hardware -- and without Windows.
///
/// The hydraulics are a deliberately crude first-order model: the outlet pressure relaxes
/// towards <c>flow × resistance</c>. The CP controller is modelled as "deliver FI until the
/// pressure reaches PS, then deliver whatever holds PS", which is enough to exercise both the
/// over-pressure trip and the flow-deviation trip. Raising <see cref="ResistancePsiPerMlMin"/>
/// mid-run imitates a blockage.
/// </summary>
public sealed class SimulatedPump : IPumpTransport
{
    public const double MaxFlowMlMin = 12.0;
    public const int FlowDecimals = 3;
    public const double MaxPressurePsi = 18_000;

    private readonly object _gate = new();
    private readonly Func<double> _clock;
    private double _lastTime;

    private double _flowLimit = 1.0;
    private double _pressureSetPoint = 5000;
    private double _upperLimit = MaxPressurePsi;
    private double _lowerLimit;
    private double _pressure;
    private double _flow;
    private bool _running;
    private bool _faultUpper;

    /// <param name="clock">Seconds, monotonic. Defaults to wall-clock time; tests pass their own.</param>
    public SimulatedPump(Func<double>? clock = null)
    {
        if (clock is null)
        {
            var sw = Stopwatch.StartNew();
            clock = () => sw.Elapsed.TotalSeconds;
        }

        _clock = clock;
        _lastTime = _clock();
    }

    public string Name => PumpConfig.SimulatorPortName;

    /// <summary>Hydraulic resistance of the simulated system, psi per mL/min.</summary>
    public double ResistancePsiPerMlMin { get; set; } = 300;

    /// <summary>Time constant of the pressure response, seconds.</summary>
    public double TimeConstantSec { get; set; } = 2.0;

    /// <summary>When set, every exchange times out, as if the cable had been pulled.</summary>
    public bool Unplugged { get; set; }

    private readonly List<string> _received = [];

    /// <summary>A copy of every command received so far, in order, for tests to inspect.</summary>
    public List<string> ReceivedCommands()
    {
        lock (_gate) return [.. _received];
    }

    public double Pressure { get { lock (_gate) { Advance(); return _pressure; } } }

    public bool Running { get { lock (_gate) { Advance(); return _running; } } }

    public double PressureSetPoint { get { lock (_gate) return _pressureSetPoint; } }

    public double UpperLimit { get { lock (_gate) return _upperLimit; } }

    public string Exchange(string command, TimeSpan timeout)
    {
        if (Unplugged) throw new TimeoutException($"Simulator unplugged: no reply to '{command}'.");

        lock (_gate)
        {
            _received.Add(command);
            Advance();
            return Respond(command.Trim().ToUpperInvariant());
        }
    }

    public void SendOnly(string command)
    {
        lock (_gate) _received.Add(command);
    }

    private string Respond(string cmd)
    {
        switch (cmd)
        {
            case "RU":
                if (!_faultUpper) _running = true;
                return "OK/";
            case "ST":
                _running = false;
                _faultUpper = false;   // the guide: ST or CF clears an active fault
                return "OK/";
            case "CF":
                _faultUpper = false;
                return "OK/";
            case "KD" or "KE":
                return "OK/";
            case "CC":
                return Inv($"OK,{Math.Round(_pressure):0000},{_flow.ToString("F" + FlowDecimals, CultureInfo.InvariantCulture)}/");
            case "CS":
                return Inv($"OK,{_flowLimit.ToString("F" + FlowDecimals, CultureInfo.InvariantCulture)},{_upperLimit:00000},{_lowerLimit:0000},psi,0,{(_running ? 1 : 0)},0/");
            case "RF":
                return $"OK,0,{(_faultUpper ? 1 : 0)},0/";
            case "ID":
                return "OK, SIMULATOR Version 1.0.0/";
            case "MF":
                return "OK,MF:" + MaxFlowMlMin.ToString("F" + FlowDecimals, CultureInfo.InvariantCulture) + "/";
            case "MP":
                return Inv($"OK,MP:{MaxPressurePsi:0}/");
            case "PU":
                return "OK,psi/";
            case "UP":
                return Inv($"OK,UP:{_upperLimit:00000}/");
            case "LP":
                return Inv($"OK,LP:{_lowerLimit:00000}/");
            case "PS":
                return Inv($"OK,PS:{_pressureSetPoint:00000}/");
            case "LS":
                return "OK,LS:0/";
        }

        if (cmd.Length > 2 && long.TryParse(cmd[2..], NumberStyles.None, CultureInfo.InvariantCulture, out long arg))
        {
            switch (cmd[..2])
            {
                case "FI":
                    _flowLimit = Math.Min(arg / Math.Pow(10, FlowDecimals), MaxFlowMlMin);
                    return Inv($"OK,FI:{Math.Round(_flowLimit * Math.Pow(10, FlowDecimals)):00000}/");
                case "UP":
                    _upperLimit = Math.Min(arg, MaxPressurePsi);
                    return "OK/";
                case "LP":
                    _lowerLimit = Math.Min(arg, MaxPressurePsi);
                    return "OK/";
                case "PS":
                    _pressureSetPoint = Math.Min(arg, MaxPressurePsi);
                    return Inv($"OK,PS:{_pressureSetPoint:00000}/");
            }
        }

        return PumpProtocol.ErrorReply;
    }

    private void Advance()
    {
        double now = _clock();
        double dt = Math.Max(0, now - _lastTime);
        _lastTime = now;
        if (dt == 0) return;

        if (_running)
        {
            // The CP controller: full FI until the set point is reached, then just enough to hold it.
            double holdFlow = _pressureSetPoint / ResistancePsiPerMlMin;
            _flow = Math.Min(_flowLimit, holdFlow);
        }
        else
        {
            _flow = 0;
        }

        double target = _flow * ResistancePsiPerMlMin;
        _pressure += (target - _pressure) * (1 - Math.Exp(-dt / TimeConstantSec));

        if (_running && _pressure > _upperLimit)
        {
            _running = false;
            _faultUpper = true;
            _flow = 0;
        }
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public void Dispose() { }
}
