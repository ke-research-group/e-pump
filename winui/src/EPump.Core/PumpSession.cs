namespace EPump.Core;

/// <summary>Something worth a line in the recording's event log.</summary>
public sealed record PumpEvent(double TimeUnix, string Kind, string Message);

/// <summary>The <see cref="PumpEvent.Kind"/> values, as written to the <c>.npz</c>.</summary>
public static class EventKinds
{
    public const string Connected = "connected";
    public const string PumpRun = "pump_run";
    public const string PumpStop = "pump_stop";
    public const string TargetSet = "target_set";
    public const string MaxFlowSet = "max_flow_set";
    public const string LimitRestored = "limit_restored";
    public const string FaultsCleared = "faults_cleared";
    public const string SafetyOverPressure = "safety_stop_overpressure";
    public const string PumpFault = "pump_fault";
    public const string LinkLost = "link_lost";
    public const string LinkRestored = "link_restored";
    public const string RecordStart = "record_start";
    public const string RecordStop = "record_stop";
}

public static class UnixTime
{
    /// <summary>Now, as Unix seconds (UTC). .NET reads the precise system time on Windows, well under a millisecond.</summary>
    public static double Now() => (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
}

/// <summary>
/// A connected pump: the poll loop, the safety rules, and every command the UI can issue.
///
/// The pump runs in its constant-pressure mode. The operator sets the target pressure (PS,
/// front panel PRESS) and the maximum flow (FI); the pump varies its flow, up to that maximum,
/// to hold the target. The upper pressure limit (UP, front panel HI PR) is not the operator's:
/// it is pinned at <see cref="PumpConfig.UpperLimitPsi"/> on connect, checked on every poll, and
/// put back if anyone raises it from the keypad.
///
/// Polling starts on connect and runs for as long as the session lives; the software safety
/// check only exists while it runs. Events are raised on the poll loop's thread.
/// </summary>
public sealed class PumpSession : IAsyncDisposable
{
    private readonly PumpClient _client;
    private readonly SafetyMonitor _safety = new();
    private readonly object _safetyGate = new();
    private readonly CancellationTokenSource _cts = new();
    private Task _pollLoop = Task.CompletedTask;

    private int _failedCycles;
    private int _cycle;
    private bool _leak;
    private bool _lastRunning;

    private PumpSession(PumpClient client) => _client = client;

    public PumpInfo Info => _client.Info!;

    public string PortName => _client.PortName;

    /// <summary>The most recent successful poll, or null before the first one.</summary>
    public PumpSample? LatestSample { get; private set; }

    /// <summary>True once <see cref="PumpConfig.CommLossCycles"/> poll cycles in a row have failed.</summary>
    public bool IsLinkLost { get; private set; }

    /// <summary>One line describing the pump and the link, for the event log.</summary>
    public string ConnectionSummary { get; private set; } = string.Empty;

    /// <summary>PS (front panel PRESS), as last read back from the pump, psi.</summary>
    public double PressureSetPointPsi { get; private set; }

    /// <summary>FI, the maximum flow, as last read back from the pump, mL/min.</summary>
    public double FlowLimit { get; private set; }

    public event Action<PumpSample>? SampleReceived;
    public event Action<SafetyVerdict>? SafetyTripped;
    public event Action<PumpEvent>? EventLogged;
    public event Action<bool, string>? LinkStateChanged;

    /// <summary>
    /// Identify the pump, pin HI PR at the cap, read its current target and maximum flow, and
    /// start polling. Throws, and leaves nothing open, if the pump does not answer or will not
    /// take the limit. The target pressure itself is only written by Run and Apply.
    /// </summary>
    public static async Task<PumpSession> ConnectAsync(IPumpTransport transport, CancellationToken ct = default)
    {
        var client = new PumpClient(transport);
        var session = new PumpSession(client);
        try
        {
            PumpInfo info = await client.IdentifyAsync(ct).ConfigureAwait(false);
            await session.PinUpperLimitAsync(ct).ConfigureAwait(false);

            session.PressureSetPointPsi = await client.ReadPressureSetPointAsync(ct).ConfigureAwait(false);
            PumpStatus status = await client.ReadStatusAsync(ct).ConfigureAwait(false);
            session.FlowLimit = status.FlowSetPoint;
            session._lastRunning = status.Running;

            // Nobody can have subscribed to EventLogged yet, so the summary is kept for the
            // caller to log rather than raised here.
            session.ConnectionSummary =
                $"{info.Identity} on {client.PortName}: max {info.MaxFlow} mL/min, {info.MaxPressurePsi:F0} psi, units {info.Units}; "
                + $"HI PR pinned at {PumpConfig.UpperLimitPsi:F0} psi, PRESS {session.PressureSetPointPsi:F0} psi"
                + (status.Running ? " (already running)" : string.Empty);

            session._pollLoop = Task.Run(() => session.PollLoopAsync(session._cts.Token));
            return session;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // ---------- commands ----------

    /// <summary>
    /// Write HI PR, the target pressure and the maximum flow, read every one of them back, and
    /// only then send Run. A pump that will not confirm its settings is not started.
    /// </summary>
    public async Task StartPumpAsync(double targetPsi, double maxFlow, CancellationToken ct = default)
    {
        SafetyMonitor.ValidateTarget(targetPsi);
        ValidateFlow(maxFlow);

        PumpFaults faults = await _client.ReadFaultsAsync(ct).ConfigureAwait(false);
        if (faults.Any)
            throw new InvalidOperationException(SafetyMonitor.DescribeFaults(faults) + " Clear the fault before running.");

        await PinUpperLimitAsync(ct).ConfigureAwait(false);
        await ApplyTargetAsync(targetPsi, ct).ConfigureAwait(false);
        await ApplyMaxFlowAsync(maxFlow, ct).ConfigureAwait(false);

        await _client.RunAsync(ct).ConfigureAwait(false);
        Log(EventKinds.PumpRun,
            $"Run: target {PressureSetPointPsi:F0} psi, max flow {FlowLimit} mL/min, HI PR {PumpConfig.UpperLimitPsi:F0} psi");
    }

    /// <summary>Send Stop. Deliberately not cancellable: a stop that is abandoned half-way is worse than one that is slow.</summary>
    public async Task StopPumpAsync(string reason = "Stopped by the operator")
    {
        await _client.StopAsync(CancellationToken.None).ConfigureAwait(false);
        Log(EventKinds.PumpStop, reason);
    }

    /// <summary>Change the target pressure, running or not.</summary>
    public async Task SetTargetPressureAsync(double targetPsi, CancellationToken ct = default)
    {
        SafetyMonitor.ValidateTarget(targetPsi);
        await ApplyTargetAsync(targetPsi, ct).ConfigureAwait(false);
        Log(EventKinds.TargetSet, $"Target pressure set to {PressureSetPointPsi:F0} psi");
    }

    /// <summary>Change the maximum flow, running or not.</summary>
    public async Task SetMaxFlowAsync(double maxFlow, CancellationToken ct = default)
    {
        ValidateFlow(maxFlow);
        await ApplyMaxFlowAsync(maxFlow, ct).ConfigureAwait(false);
        Log(EventKinds.MaxFlowSet, $"Max flow set to {FlowLimit} mL/min");
    }

    public async Task ClearFaultsAsync(CancellationToken ct = default)
    {
        await _client.ClearFaultsAsync(ct).ConfigureAwait(false);
        Log(EventKinds.FaultsCleared, "Faults cleared");
    }

    public Task SetKeypadLockedAsync(bool locked, CancellationToken ct = default) => _client.SetKeypadLockedAsync(locked, ct);

    private void ValidateFlow(double flow)
    {
        if (!double.IsFinite(flow) || flow <= 0 || flow > Info.MaxFlow)
            throw new ArgumentOutOfRangeException(nameof(flow), flow, $"The maximum flow must be above 0 and at most {Info.MaxFlow} mL/min.");
    }

    /// <summary>psi round-trips exactly; bar and MPa lose up to one count of the pump's resolution.</summary>
    private double PressureTolerance => Info.Units == PressureUnit.Psi ? 1 : 16;

    /// <summary>Write UP and read it back. If the pump will not hold the cap, the protection this application relies on is missing, so this throws.</summary>
    private async Task PinUpperLimitAsync(CancellationToken ct)
    {
        await _client.SetUpperLimitAsync(PumpConfig.UpperLimitPsi, ct).ConfigureAwait(false);
        double up = await _client.ReadUpperLimitAsync(ct).ConfigureAwait(false);
        if (up > PumpConfig.UpperLimitPsi + 0.5 || up < PumpConfig.UpperLimitPsi - PressureTolerance)
            throw new InvalidOperationException($"The pump holds HI PR at {up:F0} psi after being sent {PumpConfig.UpperLimitPsi:F0} psi.");
    }

    private async Task ApplyTargetAsync(double targetPsi, CancellationToken ct)
    {
        await _client.SetPressureSetPointAsync(targetPsi, ct).ConfigureAwait(false);
        double ps = await _client.ReadPressureSetPointAsync(ct).ConfigureAwait(false);
        if (ps > targetPsi + 0.5 || ps < targetPsi - PressureTolerance)
            throw new InvalidOperationException($"The pump holds a target (PRESS) of {ps:F0} psi after being sent {targetPsi:F0} psi.");
        PressureSetPointPsi = ps;
    }

    private async Task ApplyMaxFlowAsync(double maxFlow, CancellationToken ct)
    {
        double accepted = await _client.SetFlowAsync(maxFlow, ct).ConfigureAwait(false);
        PumpStatus status = await _client.ReadStatusAsync(ct).ConfigureAwait(false);
        double resolution = Math.Pow(10, -Info.FlowDecimals);
        if (Math.Abs(status.FlowSetPoint - maxFlow) > resolution / 2 + 1e-9)
            throw new InvalidOperationException(
                $"The pump reports a maximum flow of {status.FlowSetPoint} mL/min after being sent {maxFlow} mL/min (it accepted {accepted}).");
        FlowLimit = status.FlowSetPoint;
    }

    // ---------- polling ----------

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PumpConfig.PollIntervalMs));
        try
        {
            do
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        PumpSample sample;
        try
        {
            (double pressure, double flow) = await _client.ReadConditionsAsync(ct).ConfigureAwait(false);
            double t = UnixTime.Now();
            PumpStatus status = await _client.ReadStatusAsync(ct).ConfigureAwait(false);
            PumpFaults faults = await _client.ReadFaultsAsync(ct).ConfigureAwait(false);

            // The slow-changing values are read every few cycles to keep the cycle short.
            // PS is among them so a target changed on the keypad still shows up.
            if (_cycle++ % PumpConfig.LeakPollEvery == 0)
            {
                _leak = await _client.ReadLeakAsync(ct).ConfigureAwait(false) ?? false;
                PressureSetPointPsi = await _client.ReadPressureSetPointAsync(ct).ConfigureAwait(false);
            }

            FlowLimit = status.FlowSetPoint;
            sample = new PumpSample(t, pressure, flow, status.FlowSetPoint, PressureSetPointPsi, status.UpperLimit,
                                    status.Running, faults, _leak);
        }
        catch (Exception e) when (e is TimeoutException or PumpProtocolException or IOException
                                      or InvalidOperationException or UnauthorizedAccessException)
        {
            if (++_failedCycles == PumpConfig.CommLossCycles)
            {
                IsLinkLost = true;
                string message = $"No valid reply from the pump for {PumpConfig.CommLossCycles} poll cycles: {e.Message}";
                Log(EventKinds.LinkLost, message);
                LinkStateChanged?.Invoke(true, message);
            }
            return;
        }

        _failedCycles = 0;
        if (IsLinkLost)
        {
            IsLinkLost = false;
            Log(EventKinds.LinkRestored, "Pump answering again");
            LinkStateChanged?.Invoke(false, "Pump answering again");
        }

        LatestSample = sample;
        SampleReceived?.Invoke(sample);

        if (_lastRunning && !sample.Running && !sample.Faults.Any)
            Log(EventKinds.PumpStop, "The pump reports it has stopped");
        _lastRunning = sample.Running;

        SafetyVerdict verdict;
        lock (_safetyGate) verdict = _safety.Evaluate(sample);
        if (verdict.Tripped) await HandleTripAsync(verdict).ConfigureAwait(false);

        await EnforceLimitAsync(sample, ct).ConfigureAwait(false);
    }

    private async Task HandleTripAsync(SafetyVerdict verdict)
    {
        string kind = verdict.Reason == TripReason.OverPressure ? EventKinds.SafetyOverPressure : EventKinds.PumpFault;

        if (verdict.Reason == TripReason.OverPressure)
        {
            try
            {
                await _client.StopAsync(CancellationToken.None).ConfigureAwait(false);
                _lastRunning = false;
            }
            catch (Exception e) when (e is TimeoutException or PumpProtocolException or IOException or InvalidOperationException)
            {
                verdict = verdict with { Message = verdict.Message + $" STOP COULD NOT BE SENT ({e.Message}) -- stop the pump by hand." };
            }
        }

        Log(kind, verdict.Message);
        SafetyTripped?.Invoke(verdict);
    }

    /// <summary>HI PR raised from the front panel: put it back. Lowering it is left alone, since that is only safer.</summary>
    private async Task EnforceLimitAsync(PumpSample sample, CancellationToken ct)
    {
        if (sample.PressureLimitPsi <= PumpConfig.UpperLimitPsi + 0.5) return;

        try
        {
            await _client.SetUpperLimitAsync(PumpConfig.UpperLimitPsi, ct).ConfigureAwait(false);
            Log(EventKinds.LimitRestored,
                $"HI PR was {sample.PressureLimitPsi:F0} psi (changed on the keypad?); restored to {PumpConfig.UpperLimitPsi:F0} psi");
        }
        catch (Exception e) when (e is TimeoutException or PumpProtocolException or IOException or InvalidOperationException)
        {
            // Retried on the next cycle.
        }
    }

    private void Log(string kind, string message) => EventLogged?.Invoke(new PumpEvent(UnixTime.Now(), kind, message));

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _pollLoop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _client.Dispose();
        _cts.Dispose();
    }
}
