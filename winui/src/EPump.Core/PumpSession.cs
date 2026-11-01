namespace EPump.Core;

/// <summary>Something worth a line in the recording's event log.</summary>
public sealed record PumpEvent(double TimeUnix, string Kind, string Message);

/// <summary>The <see cref="PumpEvent.Kind"/> values, as written to the <c>.npz</c>.</summary>
public static class EventKinds
{
    public const string Connected = "connected";
    public const string PumpRun = "pump_run";
    public const string PumpStop = "pump_stop";
    public const string FlowSet = "flow_set";
    public const string LimitSet = "limit_set";
    public const string LimitRestored = "limit_restored";
    public const string FaultsCleared = "faults_cleared";
    public const string SafetyOverPressure = "safety_stop_overpressure";
    public const string SafetyFlowDeviation = "safety_stop_flow_deviation";
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
/// Connecting immediately puts the pump into the state this application relies on -- the
/// pressure set point pinned at <see cref="PumpConfig.PressureSetPointPsi"/> and the upper
/// limit at the operator's value -- and polling starts at once and runs for as long as the
/// session lives. Polling is not optional: the software safety checks only exist while it runs.
///
/// Events are raised on the poll loop's thread; UI subscribers must marshal.
/// </summary>
public sealed class PumpSession : IAsyncDisposable
{
    private readonly PumpClient _client;
    private readonly SafetyMonitor _safety;
    private readonly object _safetyGate = new();
    private readonly CancellationTokenSource _cts = new();
    private Task _pollLoop = Task.CompletedTask;

    private int _failedCycles;
    private int _cycle;
    private bool _leak;
    private bool _lastRunning;

    private PumpSession(PumpClient client, double pressureLimitPsi)
    {
        _client = client;
        _safety = new SafetyMonitor(pressureLimitPsi);
    }

    public PumpInfo Info => _client.Info!;

    public string PortName => _client.PortName;

    public double PressureLimitPsi { get { lock (_safetyGate) return _safety.PressureLimitPsi; } }

    /// <summary>The most recent successful poll, or null before the first one.</summary>
    public PumpSample? LatestSample { get; private set; }

    /// <summary>True once <see cref="PumpConfig.CommLossCycles"/> poll cycles in a row have failed.</summary>
    public bool IsLinkLost { get; private set; }

    /// <summary>One line describing the pump and the link, for the event log.</summary>
    public string ConnectionSummary { get; private set; } = string.Empty;

    /// <summary>The flow the pump last accepted for FI, mL/min.</summary>
    public double FlowSetPoint { get; private set; }

    public event Action<PumpSample>? SampleReceived;
    public event Action<SafetyVerdict>? SafetyTripped;
    public event Action<PumpEvent>? EventLogged;
    public event Action<bool, string>? LinkStateChanged;

    /// <summary>
    /// Identify the pump, pin its pressure set point and limit, and start polling. Throws, and
    /// leaves nothing open, if the pump does not answer or will not take the settings.
    /// </summary>
    public static async Task<PumpSession> ConnectAsync(IPumpTransport transport, double pressureLimitPsi, CancellationToken ct = default)
    {
        SafetyMonitor.ValidateLimit(pressureLimitPsi);
        var client = new PumpClient(transport);
        var session = new PumpSession(client, pressureLimitPsi);
        try
        {
            PumpInfo info = await client.IdentifyAsync(ct).ConfigureAwait(false);
            await session.ApplyPressureSettingsAsync(pressureLimitPsi, ct).ConfigureAwait(false);

            PumpStatus status = await client.ReadStatusAsync(ct).ConfigureAwait(false);
            session.FlowSetPoint = status.FlowSetPoint;
            session._lastRunning = status.Running;

            // Nobody can have subscribed to EventLogged yet, so the summary is kept for the
            // caller to log rather than raised here.
            session.ConnectionSummary =
                $"{info.Identity} on {client.PortName}: max {info.MaxFlow} mL/min, {info.MaxPressurePsi:F0} psi, units {info.Units}"
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
    /// Set the limit, the pinned set point and the flow, read every one of them back, and only
    /// then send Run. A pump that will not confirm its settings is not started.
    /// </summary>
    public async Task StartPumpAsync(double flow, CancellationToken ct = default)
    {
        ValidateFlow(flow);

        PumpFaults faults = await _client.ReadFaultsAsync(ct).ConfigureAwait(false);
        if (faults.Any)
            throw new InvalidOperationException(SafetyMonitor.DescribeFaults(faults) + " Clear the fault before running.");

        double limit = PressureLimitPsi;
        await ApplyPressureSettingsAsync(limit, ct).ConfigureAwait(false);

        double accepted = await _client.SetFlowAsync(flow, ct).ConfigureAwait(false);
        PumpStatus status = await _client.ReadStatusAsync(ct).ConfigureAwait(false);
        double resolution = Math.Pow(10, -Info.FlowDecimals);
        if (Math.Abs(status.FlowSetPoint - flow) > resolution / 2 + 1e-9)
            throw new InvalidOperationException(
                $"The pump reports a flow set point of {status.FlowSetPoint} mL/min after being sent {flow} mL/min (it accepted {accepted}). Not starting.");
        FlowSetPoint = status.FlowSetPoint;

        lock (_safetyGate) _safety.ArmGrace(UnixTime.Now());
        await _client.RunAsync(ct).ConfigureAwait(false);
        Log(EventKinds.PumpRun, $"Run at {flow} mL/min, limit {limit:F0} psi, set point {PumpConfig.PressureSetPointPsi:F0} psi");
    }

    /// <summary>Send Stop. Deliberately not cancellable: a stop that is abandoned half-way is worse than one that is slow.</summary>
    public async Task StopPumpAsync(string reason = "Stopped by the operator")
    {
        await _client.StopAsync(CancellationToken.None).ConfigureAwait(false);
        Log(EventKinds.PumpStop, reason);
    }

    /// <summary>Change the flow, running or not. The flow check is re-armed so the ramp is not mistaken for throttling.</summary>
    public async Task SetFlowAsync(double flow, CancellationToken ct = default)
    {
        ValidateFlow(flow);
        lock (_safetyGate) _safety.ArmGrace(UnixTime.Now());
        await _client.SetFlowAsync(flow, ct).ConfigureAwait(false);
        PumpStatus status = await _client.ReadStatusAsync(ct).ConfigureAwait(false);
        FlowSetPoint = status.FlowSetPoint;
        Log(EventKinds.FlowSet, $"Flow set to {status.FlowSetPoint} mL/min");
    }

    public async Task SetPressureLimitAsync(double psi, CancellationToken ct = default)
    {
        SafetyMonitor.ValidateLimit(psi);
        await ApplyPressureSettingsAsync(psi, ct).ConfigureAwait(false);
        Log(EventKinds.LimitSet, $"Upper pressure limit set to {psi:F0} psi");
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
            throw new ArgumentOutOfRangeException(nameof(flow), flow, $"The flow must be above 0 and at most {Info.MaxFlow} mL/min.");
    }

    /// <summary>
    /// Write UP and PS and read both back. The limit is verified to be no higher than asked
    /// (the protocol rounds down) and the set point to be the pinned value -- if either check
    /// fails, the safety assumptions of the whole application do not hold, so this throws.
    /// </summary>
    private async Task ApplyPressureSettingsAsync(double limitPsi, CancellationToken ct)
    {
        await _client.SetUpperLimitAsync(limitPsi, ct).ConfigureAwait(false);
        await _client.SetPressureSetPointAsync(PumpConfig.PressureSetPointPsi, ct).ConfigureAwait(false);

        double up = await _client.ReadUpperLimitAsync(ct).ConfigureAwait(false);
        double ps = await _client.ReadPressureSetPointAsync(ct).ConfigureAwait(false);

        // psi round-trips exactly; bar and MPa lose up to one count of the pump's resolution.
        double tolerance = Info.Units == PressureUnit.Psi ? 1 : 16;
        if (up > limitPsi + 0.5 || up < limitPsi - tolerance)
            throw new InvalidOperationException($"The pump holds an upper limit of {up:F0} psi after being sent {limitPsi:F0} psi.");
        if (ps > PumpConfig.PressureCapPsi + 0.5 || ps < PumpConfig.PressureSetPointPsi - tolerance)
            throw new InvalidOperationException($"The pump holds a pressure set point of {ps:F0} psi after being sent {PumpConfig.PressureSetPointPsi:F0} psi.");

        lock (_safetyGate) _safety.PressureLimitPsi = limitPsi;
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
            if (_cycle++ % PumpConfig.LeakPollEvery == 0)
                _leak = await _client.ReadLeakAsync(ct).ConfigureAwait(false) ?? false;

            sample = new PumpSample(t, pressure, flow, status.FlowSetPoint, status.UpperLimit, status.Running, faults, _leak);
            FlowSetPoint = status.FlowSetPoint;
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
        string kind = verdict.Reason switch
        {
            TripReason.OverPressure => EventKinds.SafetyOverPressure,
            TripReason.FlowDeviation => EventKinds.SafetyFlowDeviation,
            _ => EventKinds.PumpFault,
        };

        if (verdict.Reason != TripReason.PumpFault)
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

    /// <summary>Someone raised the limit from the front panel: put it back.</summary>
    private async Task EnforceLimitAsync(PumpSample sample, CancellationToken ct)
    {
        double limit = PressureLimitPsi;
        if (sample.PressureLimitPsi <= limit + 0.5) return;

        try
        {
            await _client.SetUpperLimitAsync(limit, ct).ConfigureAwait(false);
            Log(EventKinds.LimitRestored,
                $"The pump's upper limit was {sample.PressureLimitPsi:F0} psi (changed on the keypad?); restored to {limit:F0} psi");
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
