using System.Diagnostics;

namespace EPump.Core;

/// <summary>What the pump says about itself on connect.</summary>
public sealed record PumpInfo(
    string Identity, double MaxFlow, int FlowDecimals, double MaxPressurePsi, PressureUnit Units);

/// <summary>
/// Typed commands on top of an <see cref="IPumpTransport"/>, following the timing rules of
/// the SSI communication guide (section 4):
///
/// - one command at a time, and the reply is awaited before the next one is sent;
/// - at least <see cref="PumpConfig.MinCommandGapMs"/> between transmissions;
/// - an <c>Er/</c> reply or a timeout is retried up to <see cref="PumpConfig.CommandAttempts"/> times.
///
/// Thread-safe: the poll loop and the UI's commands share one client, and every exchange is
/// serialised through a single lock, so a Stop issued from the UI waits for at most one
/// in-flight command.
/// </summary>
public sealed class PumpClient : IDisposable
{
    private readonly IPumpTransport _transport;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _sinceLast = Stopwatch.StartNew();
    private bool _disposed;

    public PumpClient(IPumpTransport transport)
    {
        _transport = transport;
        // Let the very first command go out immediately.
        _lastSentMs = -PumpConfig.MinCommandGapMs;
    }

    private long _lastSentMs;

    public string PortName => _transport.Name;

    /// <summary>The configuration read by <see cref="IdentifyAsync"/>; null until then.</summary>
    public PumpInfo? Info { get; private set; }

    /// <summary>Unit the pump talks in. Pressures in and out of this class are always psi.</summary>
    private PressureUnit Units => Info?.Units ?? PressureUnit.Psi;

    // ---------- connect ----------

    /// <summary>Flush the pump's receive buffer, then read its identity and capabilities.</summary>
    public async Task<PumpInfo> IdentifyAsync(CancellationToken ct = default)
    {
        await SendOnlyAsync(PumpProtocol.ClearBuffer, ct).ConfigureAwait(false);

        string identity = PumpProtocol.ParseIdentity(await ExchangeAsync(PumpProtocol.Identify, ct).ConfigureAwait(false));
        (double maxFlow, int decimals) = PumpProtocol.ParseMaxFlow(await ExchangeAsync(PumpProtocol.MaxFlow, ct).ConfigureAwait(false));
        PressureUnit units = PumpProtocol.ParseUnits(await ExchangeAsync(PumpProtocol.PressureUnits, ct).ConfigureAwait(false));
        double maxPressure = PumpProtocol.ParseTaggedNumber(await ExchangeAsync(PumpProtocol.MaxPressure, ct).ConfigureAwait(false), PumpProtocol.MaxPressure);

        Info = new PumpInfo(identity, maxFlow, decimals, PumpProtocol.ToPsi(maxPressure, units), units);
        return Info;
    }

    // ---------- monitoring ----------

    /// <summary><c>CC</c>: live pressure (psi) and flow (mL/min).</summary>
    public async Task<(double PressurePsi, double Flow)> ReadConditionsAsync(CancellationToken ct = default)
    {
        (double p, double f) = PumpProtocol.ParseConditions(await ExchangeAsync(PumpProtocol.CurrentConditions, ct).ConfigureAwait(false));
        return (PumpProtocol.ToPsi(p, Units), f);
    }

    /// <summary><c>CS</c>, with the limits converted to psi.</summary>
    public async Task<PumpStatus> ReadStatusAsync(CancellationToken ct = default)
    {
        PumpStatus s = PumpProtocol.ParseStatus(await ExchangeAsync(PumpProtocol.CurrentStatus, ct).ConfigureAwait(false));
        return s with
        {
            UpperLimit = PumpProtocol.ToPsi(s.UpperLimit, s.Units),
            LowerLimit = PumpProtocol.ToPsi(s.LowerLimit, s.Units),
        };
    }

    public async Task<PumpFaults> ReadFaultsAsync(CancellationToken ct = default) =>
        PumpProtocol.ParseFaults(await ExchangeAsync(PumpProtocol.ReadFaults, ct).ConfigureAwait(false));

    /// <summary><c>LS</c>. Pumps without a leak sensor answer <c>Er/</c>, which is reported as null rather than retried forever.</summary>
    public async Task<bool?> ReadLeakAsync(CancellationToken ct = default)
    {
        string reply = await ExchangeAsync(PumpProtocol.LeakStatus, ct, retryOnError: false).ConfigureAwait(false);
        return PumpProtocol.IsOk(reply) ? PumpProtocol.ParseLeak(reply) : null;
    }

    public async Task<double> ReadUpperLimitAsync(CancellationToken ct = default) =>
        PumpProtocol.ToPsi(PumpProtocol.ParseTaggedNumber(await ExchangeAsync(PumpProtocol.UpperLimit, ct).ConfigureAwait(false), PumpProtocol.UpperLimit), Units);

    public async Task<double> ReadPressureSetPointAsync(CancellationToken ct = default) =>
        PumpProtocol.ToPsi(PumpProtocol.ParseTaggedNumber(await ExchangeAsync(PumpProtocol.PressureSetPoint, ct).ConfigureAwait(false), PumpProtocol.PressureSetPoint), Units);

    // ---------- control ----------

    public Task RunAsync(CancellationToken ct = default) => ExpectOkAsync(PumpProtocol.Run, ct);

    public Task StopAsync(CancellationToken ct = default) => ExpectOkAsync(PumpProtocol.Stop, ct);

    public Task ClearFaultsAsync(CancellationToken ct = default) => ExpectOkAsync(PumpProtocol.ClearFaults, ct);

    public Task SetKeypadLockedAsync(bool locked, CancellationToken ct = default) =>
        ExpectOkAsync(locked ? PumpProtocol.KeypadDisable : PumpProtocol.KeypadEnable, ct);

    /// <summary><c>FI</c>. Returns the flow the pump actually accepted (it clamps to its maximum).</summary>
    public async Task<double> SetFlowAsync(double mlPerMin, CancellationToken ct = default)
    {
        PumpInfo info = Info ?? throw new InvalidOperationException("Identify the pump before setting the flow.");
        string reply = await ExpectOkAsync(PumpProtocol.SetFlow(mlPerMin, info.FlowDecimals), ct).ConfigureAwait(false);
        return PumpProtocol.ParseTaggedNumber(reply, "FI") / Math.Pow(10, info.FlowDecimals);
    }

    public Task SetUpperLimitAsync(double psi, CancellationToken ct = default) =>
        ExpectOkAsync(PumpProtocol.SetUpperLimit(psi, Units), ct);

    public Task SetPressureSetPointAsync(double psi, CancellationToken ct = default) =>
        ExpectOkAsync(PumpProtocol.SetPressureSetPoint(psi, Units), ct);

    // ---------- plumbing ----------

    private async Task<string> ExpectOkAsync(string command, CancellationToken ct)
    {
        string reply = await ExchangeAsync(command, ct).ConfigureAwait(false);
        return PumpProtocol.IsOk(reply)
            ? reply
            : throw new PumpProtocolException($"'{command}' was rejected by the pump ('{reply}').");
    }

    private async Task<string> ExchangeAsync(string command, CancellationToken ct, bool retryOnError = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Exception? last = null;
            for (int attempt = 1; attempt <= PumpConfig.CommandAttempts; attempt++)
            {
                await WaitForGapAsync(ct).ConfigureAwait(false);
                try
                {
                    string reply = await Task.Run(
                        () => _transport.Exchange(command, TimeSpan.FromMilliseconds(PumpConfig.ResponseTimeoutMs)), ct)
                        .ConfigureAwait(false);
                    _lastSentMs = _sinceLast.ElapsedMilliseconds;

                    if (PumpProtocol.IsOk(reply) || !retryOnError) return reply;
                    last = new PumpProtocolException($"'{command}' answered '{reply}'.");
                }
                catch (TimeoutException e)
                {
                    _lastSentMs = _sinceLast.ElapsedMilliseconds;
                    last = e;
                }
            }

            throw last is TimeoutException
                ? new TimeoutException($"'{command}': no reply after {PumpConfig.CommandAttempts} attempts.", last)
                : last ?? new PumpProtocolException($"'{command}' failed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SendOnlyAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitForGapAsync(ct).ConfigureAwait(false);
            _transport.SendOnly(command);
            _lastSentMs = _sinceLast.ElapsedMilliseconds;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task WaitForGapAsync(CancellationToken ct)
    {
        long wait = _lastSentMs + PumpConfig.MinCommandGapMs - _sinceLast.ElapsedMilliseconds;
        return wait > 0 ? Task.Delay(TimeSpan.FromMilliseconds(wait), ct) : Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.Dispose();
        _gate.Dispose();
    }
}
