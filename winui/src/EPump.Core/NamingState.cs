using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EPump.Core;

/// <summary>
/// The NI6451 application's naming memory, copied verbatim so this application reads
/// <c>%LOCALAPPDATA%\Ni6451\naming.json</c> exactly the way NI6451 writes it: one experiment
/// serial per day, with the run number counting up within that day.
///
/// This application only ever loads that file; see <see cref="SharedNaming"/>. It never calls
/// <see cref="Save"/> on it, so the numbering stays NI6451's to advance.
/// </summary>
public sealed class NamingState
{
    public const string FileName = "naming.json";

    /// <summary>Defaults the Python version shipped with, used before any run has been recorded.</summary>
    public const string DefaultExperimentSerial = "0207";

    public const int DefaultRunNumber = 1;

    /// <summary>Four-digit, zero-padded experiment serial of the most recent run.</summary>
    public string ExperimentSerial { get; set; } = DefaultExperimentSerial;

    /// <summary>Run number of the most recent run.</summary>
    public int RunNumber { get; set; } = DefaultRunNumber;

    /// <summary>Local calendar date of the most recent run, ISO <c>yyyy-MM-dd</c>; empty if none yet.</summary>
    public string LastRunDate { get; set; } = string.Empty;

    /// <summary>
    /// The naming the next run should start from, given today's date:
    /// same day as the last run → same serial, run + 1;
    /// a different day → serial + 1, run 1;
    /// no previous run recorded → the stored values as they are.
    /// </summary>
    public (string ExperimentSerial, int RunNumber) NextFor(DateOnly today)
    {
        if (string.IsNullOrEmpty(LastRunDate))
            return (Pad(ExperimentSerial), RunNumber);

        if (LastRunDate == Format(today))
            return (Pad(ExperimentSerial), RunNumber + 1);

        return (Increment(ExperimentSerial), DefaultRunNumber);
    }

    /// <summary>Record that a run with this naming started today.</summary>
    public void RecordRun(string experimentSerial, int runNumber, DateOnly today)
    {
        ExperimentSerial = Pad(experimentSerial);
        RunNumber = runNumber;
        LastRunDate = Format(today);
    }

    public static string Format(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Zero-pad to four digits; leaves non-numeric or over-long input untouched.</summary>
    public static string Pad(string serial)
    {
        string s = serial.Trim();
        return s.Length < 4 && s.All(char.IsAsciiDigit) ? s.PadLeft(4, '0') : s;
    }

    /// <summary>Serial + 1, keeping the width. A non-numeric serial is returned unchanged.</summary>
    public static string Increment(string serial)
    {
        string s = Pad(serial);
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n)) return s;
        return (n + 1).ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(4, s.Length), '0');
    }

    // ---------- persistence ----------

    public static NamingState Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new NamingState();
            return JsonSerializer.Deserialize(File.ReadAllText(path), NamingStateJsonContext.Default.NamingState)
                   ?? new NamingState();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new NamingState();   // a corrupt file must not stop the app from starting
        }
    }

    /// <summary>Write via a temporary file and an atomic replace. Failures are swallowed: losing the memory is a nuisance, not a fault.</summary>
    public void Save(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, NamingStateJsonContext.Default.NamingState));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(NamingState))]
internal partial class NamingStateJsonContext : JsonSerializerContext
{
}
