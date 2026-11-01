using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EPump.Core;

/// <summary>
/// The per-sample arrays of a recording, in spool-row order. The flags are 0/1 values stored
/// as <c>int64</c> in the <c>.npz</c>; everything else is <c>float64</c>.
/// </summary>
public static class SampleColumns
{
    public static readonly string[] Names =
    [
        "t_unix",
        "pressure_psi",
        "shear_stress_mpa",
        "flow_ml_min",
        "flow_setpoint_ml_min",
        "pressure_limit_psi",
        "running",
        "fault_motor_stall",
        "fault_over_pressure",
        "fault_under_pressure",
        "leak",
    ];

    private static readonly HashSet<string> Flags =
        ["running", "fault_motor_stall", "fault_over_pressure", "fault_under_pressure", "leak"];

    /// <summary>Decided by name rather than position, so a spool written by an older version still recovers correctly.</summary>
    public static bool IsFlag(string name) => Flags.Contains(name);

    public static void ToRow(PumpSample s, FaultGeometry geometry, Span<double> row)
    {
        row[0] = s.TimeUnix;
        row[1] = s.PressurePsi;
        row[2] = geometry.ShearStressMPa(s.PressurePsi);
        row[3] = s.Flow;
        row[4] = s.FlowSetPoint;
        row[5] = s.PressureLimitPsi;
        row[6] = s.Running ? 1 : 0;
        row[7] = s.Faults.MotorStall ? 1 : 0;
        row[8] = s.Faults.OverPressure ? 1 : 0;
        row[9] = s.Faults.UnderPressure ? 1 : 0;
        row[10] = s.Leak ? 1 : 0;
    }
}

/// <summary>
/// Everything about a recording that is known when it starts. Written beside the spool as
/// <c>manifest.json</c>, so an interrupted recording can still be turned into a proper
/// <c>.npz</c> -- the same arrangement as the NI6451 application's spool manifest.
/// </summary>
public sealed class RecordingManifest
{
    public const string FileName = "manifest.json";

    /// <summary>2 added <c>shear_stress_mpa</c> and the fault geometry.</summary>
    public int FormatVersion { get; set; } = 2;
    public string ExperimentSerial { get; set; } = "0000";
    public int Run { get; set; }
    public double StartTimeUnix { get; set; }

    /// <summary>Local start time, <c>yyyyMMdd_HHmmss</c>; the file name's timestamp.</summary>
    public string StartStamp { get; set; } = string.Empty;

    public string Port { get; set; } = string.Empty;
    public string PumpIdentity { get; set; } = string.Empty;
    public double MaxFlow { get; set; }
    public double MaxPressurePsi { get; set; }
    public string PressureUnitsNative { get; set; } = "psi";
    public double PressureCapPsi { get; set; } = PumpConfig.PressureCapPsi;
    public double PressureSetPointPsi { get; set; } = PumpConfig.PressureSetPointPsi;
    public int PollIntervalMs { get; set; } = PumpConfig.PollIntervalMs;
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>"1D" or "2D".</summary>
    public string FaultType { get; set; } = "1D";

    /// <summary>The effective fault thickness used for the shear stress (2D is always 0.5 m).</summary>
    public double FaultThicknessM { get; set; } = 0.1;

    public string[] Columns { get; set; } = SampleColumns.Names;

    [JsonIgnore]
    public FaultGeometry Geometry => new(FaultTypeExtensions.Parse(FaultType), FaultThicknessM);

    /// <summary><c>T0207-pump-run5-20261101_143000.npz</c>: the NI6451 naming, with <c>pump</c> where it has <c>raw</c>.</summary>
    public string OutputFileName =>
        $"T{ExperimentSerial}-pump-run{Run.ToString(CultureInfo.InvariantCulture)}-{StartStamp}.npz";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RecordingManifest))]
internal partial class RecordingJsonContext : JsonSerializerContext
{
}

/// <summary>Separate from <see cref="RecordingJsonContext"/>: the event log is one JSON object per line, so it must not be indented.</summary>
[JsonSerializable(typeof(PumpEvent))]
internal partial class EventJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Streams one recording to disk while it runs: a <c>samples.f64</c> file of fixed-width rows,
/// an <c>events.jsonl</c> log and the manifest, in a hidden folder inside the output folder.
/// Every sample is flushed to the OS as it arrives (it is 88 bytes every half second) and the
/// file is fsync'd every <see cref="PumpConfig.DurableFlushIntervalSec"/>, so an application
/// crash loses nothing and a power cut at most a few seconds.
///
/// Thread-safe: samples arrive on the poll thread, events from anywhere.
/// </summary>
public sealed class RecordingSpool : IDisposable
{
    public const string DirPrefix = ".epump-spool-";
    public const string SamplesFile = "samples.f64";
    public const string EventsFile = "events.jsonl";

    private readonly object _gate = new();
    private readonly FileStream _samples;
    private readonly StreamWriter _events;
    private readonly double[] _row = new double[SampleColumns.Names.Length];
    private DateTime _lastDurableFlush = DateTime.UtcNow;
    private readonly FaultGeometry _geometry;
    private bool _closed;

    public RecordingSpool(string saveDir, RecordingManifest manifest)
    {
        Manifest = manifest;
        _geometry = manifest.Geometry;
        Directory = Path.Combine(saveDir, DirPrefix + Path.GetFileNameWithoutExtension(manifest.OutputFileName));
        System.IO.Directory.CreateDirectory(Directory);

        File.WriteAllText(Path.Combine(Directory, RecordingManifest.FileName),
            JsonSerializer.Serialize(manifest, RecordingJsonContext.Default.RecordingManifest));

        _samples = new FileStream(Path.Combine(Directory, SamplesFile), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _events = new StreamWriter(new FileStream(Path.Combine(Directory, EventsFile), FileMode.CreateNew, FileAccess.Write, FileShare.Read));
    }

    public string Directory { get; }

    public RecordingManifest Manifest { get; }

    public long SampleCount { get; private set; }

    public void Append(PumpSample sample)
    {
        lock (_gate)
        {
            if (_closed) return;
            SampleColumns.ToRow(sample, _geometry, _row);
            _samples.Write(MemoryMarshal.AsBytes(_row.AsSpan()));
            SampleCount++;

            bool durable = DateTime.UtcNow - _lastDurableFlush >= TimeSpan.FromSeconds(PumpConfig.DurableFlushIntervalSec);
            _samples.Flush(flushToDisk: durable);
            if (durable)
            {
                _events.Flush();
                _lastDurableFlush = DateTime.UtcNow;
            }
        }
    }

    public void AppendEvent(PumpEvent e)
    {
        lock (_gate)
        {
            if (_closed) return;
            _events.WriteLine(JsonSerializer.Serialize(e, EventJsonContext.Default.PumpEvent));
            _events.Flush();
        }
    }

    /// <summary>Close the files. The spool stays on disk until <see cref="RecordingFinalizer"/> has turned it into an <c>.npz</c>.</summary>
    public void Close()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _samples.Flush(flushToDisk: true);
            _samples.Dispose();
            _events.Flush();
            _events.Dispose();
        }
    }

    /// <summary>Close and delete the spool: for a recording that should never become a file (the pump did not start).</summary>
    public void Discard()
    {
        Close();
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose() => Close();
}

/// <summary>A spool folder whose recording never reached its <c>.npz</c>.</summary>
public sealed record OrphanedRecording(string SpoolDir, RecordingManifest Manifest, long Samples);

/// <summary>Turns a closed spool -- or one left behind by a crash -- into the final <c>.npz</c>.</summary>
public static class RecordingFinalizer
{
    /// <summary>
    /// Write the archive into <paramref name="saveDir"/> and delete the spool. If anything fails
    /// the partial <c>.npz</c> is removed and the spool is kept, so the data can be recovered later.
    /// </summary>
    public static string Finalize(string spoolDir, string saveDir)
    {
        RecordingManifest manifest = LoadManifest(spoolDir)
            ?? throw new InvalidDataException($"'{spoolDir}' has no readable {RecordingManifest.FileName}.");

        string outPath = UniquePath(Path.Combine(saveDir, manifest.OutputFileName));
        try
        {
            double[][] columns = ReadColumns(spoolDir, manifest.Columns.Length);
            List<PumpEvent> events = ReadEvents(spoolDir);

            using (var npz = new NpzWriter(outPath))
            {
                for (int c = 0; c < manifest.Columns.Length; c++)
                {
                    if (SampleColumns.IsFlag(manifest.Columns[c]))
                        npz.AddInt64Array(manifest.Columns[c], columns[c].Select(v => (long)v).ToArray());
                    else
                        npz.AddFloat64Array(manifest.Columns[c], columns[c]);
                }

                npz.AddFloat64Array("event_t_unix", events.Select(e => e.TimeUnix).ToArray());
                npz.AddStringArray("event_kind", events.Select(e => e.Kind).ToArray());
                npz.AddStringArray("event_message", events.Select(e => e.Message).ToArray());

                npz.AddFloat64Scalar("start_time_unix", manifest.StartTimeUnix);
                npz.AddString("experiment_serial", manifest.ExperimentSerial);
                npz.AddInt64Scalar("run", manifest.Run);
                npz.AddString("port", manifest.Port);
                npz.AddString("pump_identity", manifest.PumpIdentity);
                npz.AddString("pressure_units_native", manifest.PressureUnitsNative);
                npz.AddFloat64Scalar("max_flow_ml_min", manifest.MaxFlow);
                npz.AddFloat64Scalar("max_pressure_psi", manifest.MaxPressurePsi);
                npz.AddFloat64Scalar("pressure_cap_psi", manifest.PressureCapPsi);
                npz.AddFloat64Scalar("pressure_set_point_psi", manifest.PressureSetPointPsi);
                npz.AddInt64Scalar("poll_interval_ms", manifest.PollIntervalMs);
                npz.AddString("app_version", manifest.AppVersion);
                npz.AddString("fault_type", manifest.FaultType);
                npz.AddFloat64Scalar("fault_thickness_m", manifest.FaultThicknessM);
            }
        }
        catch
        {
            try { File.Delete(outPath); } catch (IOException) { }
            throw;
        }

        System.IO.Directory.Delete(spoolDir, recursive: true);
        return outPath;
    }

    public static IReadOnlyList<OrphanedRecording> FindOrphans(string saveDir)
    {
        if (!System.IO.Directory.Exists(saveDir)) return [];

        var result = new List<OrphanedRecording>();
        foreach (string dir in System.IO.Directory.EnumerateDirectories(saveDir, RecordingSpool.DirPrefix + "*"))
        {
            RecordingManifest? m = LoadManifest(dir);
            if (m is null) continue;
            result.Add(new OrphanedRecording(dir, m, RecoverableRows(dir, m.Columns.Length)));
        }
        return result;
    }

    public static RecordingManifest? LoadManifest(string spoolDir)
    {
        try
        {
            string path = Path.Combine(spoolDir, RecordingManifest.FileName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), RecordingJsonContext.Default.RecordingManifest)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Whole rows only: a row cut off by a crash mid-write is dropped.</summary>
    private static long RecoverableRows(string spoolDir, int columnCount)
    {
        var file = new FileInfo(Path.Combine(spoolDir, RecordingSpool.SamplesFile));
        return file.Exists ? file.Length / (columnCount * sizeof(double)) : 0;
    }

    private static double[][] ReadColumns(string spoolDir, int columnCount)
    {
        long rows = RecoverableRows(spoolDir, columnCount);
        var columns = new double[columnCount][];
        for (int c = 0; c < columnCount; c++) columns[c] = new double[rows];
        if (rows == 0) return columns;

        byte[] bytes = File.ReadAllBytes(Path.Combine(spoolDir, RecordingSpool.SamplesFile));
        ReadOnlySpan<double> all = MemoryMarshal.Cast<byte, double>(bytes.AsSpan(0, (int)(rows * columnCount * sizeof(double))));
        for (long r = 0; r < rows; r++)
            for (int c = 0; c < columnCount; c++)
                columns[c][r] = all[(int)(r * columnCount + c)];
        return columns;
    }

    private static List<PumpEvent> ReadEvents(string spoolDir)
    {
        var events = new List<PumpEvent>();
        string path = Path.Combine(spoolDir, RecordingSpool.EventsFile);
        if (!File.Exists(path)) return events;

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize(line, EventJsonContext.Default.PumpEvent) is { } e) events.Add(e);
            }
            catch (JsonException)
            {
                // A line cut off by a crash; everything before it is still good.
            }
        }
        return events;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem}-{i}.npz");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
