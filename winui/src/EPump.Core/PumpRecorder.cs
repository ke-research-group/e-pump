using System.Globalization;

namespace EPump.Core;

/// <summary>
/// Connects a <see cref="PumpSession"/> to a <see cref="RecordingSpool"/> for the length of one
/// recording. Recording is independent of the pump: it can start before Run and continue after
/// Stop, and the pump's own events (runs, stops, safety trips) land in the same event log.
/// </summary>
public sealed class PumpRecorder
{
    private readonly PumpSession _session;

    private PumpRecorder(PumpSession session, RecordingSpool spool)
    {
        _session = session;
        Spool = spool;
    }

    public RecordingSpool Spool { get; }

    public static PumpRecorder Start(PumpSession session, string saveDir, string experimentSerial, int run, string appVersion)
    {
        DateTime startLocal = DateTime.Now;
        var manifest = new RecordingManifest
        {
            ExperimentSerial = NamingState.Pad(experimentSerial),
            Run = run,
            StartTimeUnix = UnixTime.Now(),
            StartStamp = startLocal.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture),
            Port = session.PortName,
            PumpIdentity = session.Info.Identity,
            MaxFlow = session.Info.MaxFlow,
            MaxPressurePsi = session.Info.MaxPressurePsi,
            PressureUnitsNative = session.Info.Units.ToString().ToLowerInvariant(),
            AppVersion = appVersion,
        };

        var recorder = new PumpRecorder(session, new RecordingSpool(saveDir, manifest));
        recorder.Spool.AppendEvent(new PumpEvent(manifest.StartTimeUnix, EventKinds.Connected, session.ConnectionSummary));
        recorder.Spool.AppendEvent(new PumpEvent(manifest.StartTimeUnix, EventKinds.RecordStart,
            $"Recording T{manifest.ExperimentSerial} run {run}; limit {session.PressureLimitPsi:F0} psi, flow set point {session.FlowSetPoint} mL/min"));

        session.SampleReceived += recorder.Spool.Append;
        session.EventLogged += recorder.Spool.AppendEvent;
        return recorder;
    }

    /// <summary>Detach and close the spool. Returns its folder, ready for <see cref="RecordingFinalizer.Finalize"/>.</summary>
    public string Stop()
    {
        _session.SampleReceived -= Spool.Append;
        _session.EventLogged -= Spool.AppendEvent;
        Spool.AppendEvent(new PumpEvent(UnixTime.Now(), EventKinds.RecordStop, $"{Spool.SampleCount} samples"));
        Spool.Close();
        return Spool.Directory;
    }
}
