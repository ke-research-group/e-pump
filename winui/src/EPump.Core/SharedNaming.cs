namespace EPump.Core;

/// <summary>Naming the pump recording should start from, and why.</summary>
public readonly record struct NamingSuggestion(string ExperimentSerial, int Run, string Hint);

/// <summary>
/// Pump recordings share the experiment serial and run number of the NI6451 recording they
/// accompany, so <c>T0207-raw-run5-….npz</c> and <c>T0207-pump-run5-….npz</c> pair up by name.
///
/// NI6451 records a run in its naming file the moment Record is pressed, but the file holds
/// only the date, not whether that run is still going. The rule assumes the usual order --
/// Record on NI6451 first, then on the pump:
/// - NI6451 has recorded a run today → use that same run (it is the one in progress);
/// - otherwise → the serial and run NI6451 will use for its next run.
/// The fields stay editable, and the hint says which case applied.
/// </summary>
public static class SharedNaming
{
    public static string Ni6451NamingPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ni6451", NamingState.FileName);

    public static NamingSuggestion Suggest(DateOnly today) =>
        Suggest(File.Exists(Ni6451NamingPath) ? NamingState.Load(Ni6451NamingPath) : null, today);

    public static NamingSuggestion Suggest(NamingState? ni6451, DateOnly today)
    {
        if (ni6451 is null || string.IsNullOrEmpty(ni6451.LastRunDate))
        {
            return new NamingSuggestion(
                NamingState.DefaultExperimentSerial, NamingState.DefaultRunNumber,
                "No NI6451 run found on this machine. Check the serial and run before recording.");
        }

        string serial = NamingState.Pad(ni6451.ExperimentSerial);
        if (ni6451.LastRunDate == NamingState.Format(today))
        {
            return new NamingSuggestion(serial, ni6451.RunNumber,
                $"Matches NI6451's latest run today: T{serial} run {ni6451.RunNumber}. "
                + "If NI6451 has not started this run yet, use the next number.");
        }

        (string nextSerial, int nextRun) = ni6451.NextFor(today);
        return new NamingSuggestion(nextSerial, nextRun,
            $"NI6451 has not recorded today (last: T{serial} run {ni6451.RunNumber} on {ni6451.LastRunDate}). "
            + "This is the number its next run will use.");
    }
}
