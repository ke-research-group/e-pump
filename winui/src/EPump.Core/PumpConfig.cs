namespace EPump.Core;

/// <summary>
/// All tunable parameters live here, the way <c>AppConfig</c> does in the NI6451 application.
/// The safety numbers in particular are meant to be read and changed in one place.
/// </summary>
public static class PumpConfig
{
    // ---- safety ----

    /// <summary>
    /// Absolute ceiling for anything pressure-related in this application. 2800 psi is what the
    /// lab's plumbing is rated for. The pump's upper pressure limit (front panel HI PR) is pinned
    /// here, and the target pressure the operator sets can never be above it.
    /// </summary>
    public const double PressureCapPsi = 2800;

    /// <summary>
    /// HI PR, the pressure at which the pump stops itself and faults. Pinned at the cap and
    /// never offered to the operator; the software stops the pump at the same pressure.
    /// </summary>
    public const double UpperLimitPsi = PressureCapPsi;

    /// <summary>
    /// The pump runs in its constant-pressure mode: it varies its flow, up to the FI maximum,
    /// to hold the PS target (front panel PRESS). These are the defaults before the operator
    /// has set anything.
    /// </summary>
    public const double DefaultTargetPressurePsi = 1000;

    public const double DefaultMaxFlowMlMin = 1.0;

    /// <summary>Consecutive failed poll cycles before the link is reported as lost.</summary>
    public const int CommLossCycles = 3;

    // ---- serial protocol (SSI document 903068 rev F) ----

    public const int BaudRate = 9600;

    /// <summary>The guide's minimum interval between successive transmissions.</summary>
    public const int MinCommandGapMs = 100;

    /// <summary>The pump answers in ~15 ms; anything past this is a lost reply.</summary>
    public const int ResponseTimeoutMs = 500;

    /// <summary>The guide's recommendation: re-issue a command up to three times on an error reply.</summary>
    public const int CommandAttempts = 3;

    /// <summary>Target period of one poll cycle (CC + CS + RF, and LS every few cycles).</summary>
    public const int PollIntervalMs = 500;

    /// <summary>The leak sensor is read on every Nth poll cycle, to keep the cycle short.</summary>
    public const int LeakPollEvery = 4;

    /// <summary>Port-list entry that connects to the built-in simulated pump instead of hardware.</summary>
    public const string SimulatorPortName = "Simulator";

    // ---- recording ----

    /// <summary>
    /// When the pump stops by itself (a safety stop, a fault, or the front-panel key) the
    /// recording runs on this long, to capture the pressure decay, and is then saved.
    /// </summary>
    public const double RecordTailSeconds = 5;

    /// <summary>How often the spool file is fsync'd to the drive (it is flushed to the OS after every sample).</summary>
    public const int DurableFlushIntervalSec = 10;

    // ---- live plot ----

    /// <summary>Plot windows offered in the UI, in seconds.</summary>
    public static readonly int[] PlotWindowsSec = [60, 300, 900, 3600];

    public const int DefaultPlotWindowSec = 300;

    /// <summary>Trend buffer capacity: the longest window at the poll rate, with headroom.</summary>
    public static int TrendCapacity => PlotWindowsSec.Max() * 1000 / PollIntervalMs * 2;

    /// <summary>Hard cap on points drawn per trace per frame.</summary>
    public const int MaxPlotPoints = 2000;

    // ---- live-plot palette, light and dark, matching the NI6451 application ----

    public const string PlotFaceColor = "#FFFFFF";
    public const string PlotGridColor = "#E4E4E4";
    public const string PlotLabelColor = "#6E6E6E";
    public const string PressureTraceColor = "#1F77B4";
    public const string FlowTraceColor = "#2CA02C";
    public const string LimitLineColor = "#C42B1C";

    public const string PlotFaceColorDark = "#1B1B1B";
    public const string PlotGridColorDark = "#2E2E2E";
    public const string PlotLabelColorDark = "#9A9A9A";
    public const string PressureTraceColorDark = "#4DA6E8";
    public const string FlowTraceColorDark = "#5CC85C";
    public const string LimitLineColorDark = "#FF6B5B";
}
