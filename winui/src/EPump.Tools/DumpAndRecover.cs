using System.Globalization;
using System.Text;
using EPump.Core;

namespace EPump.Tools;

internal static class DumpCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: epump dump <file.npz> [--csv <out.csv>]");
            return 2;
        }

        string? csv = null;
        for (int i = 1; i < args.Length; i++)
            if (args[i] == "--csv" && i + 1 < args.Length) csv = args[++i];

        using var npz = new NpzReader(args[0]);
        foreach (string name in npz.Names.OrderBy(n => n, StringComparer.Ordinal))
        {
            NpyHeader h = npz.GetHeader(name);
            string shape = h.Shape.Length == 0 ? "()" : $"({string.Join(", ", h.Shape)},)";
            string preview = h.Shape.Length == 0 ? " = " + ScalarText(npz, name, h) : string.Empty;
            Console.WriteLine($"{name,-24} {h.Descr,-6} {shape}{preview}");
        }

        if (npz.Contains("t_unix"))
        {
            double[] t = npz.ReadFloat64Array("t_unix");
            double[] p = npz.ReadFloat64Array("pressure_psi");
            double[] f = npz.ReadFloat64Array("flow_ml_min");
            if (t.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"{t.Length} samples over {t[^1] - t[0]:F1} s, "
                    + $"from {DateTimeOffset.FromUnixTimeMilliseconds((long)(t[0] * 1000)).ToLocalTime():yyyy-MM-dd HH:mm:ss.fff}");
                Console.WriteLine($"pressure {p.Min():F0} … {p.Max():F0} psi, flow {f.Min()} … {f.Max()} mL/min");
            }
        }

        if (npz.Contains("event_kind"))
        {
            double[] et = npz.ReadFloat64Array("event_t_unix");
            string[] kind = npz.ReadStringArray("event_kind");
            string[] msg = npz.ReadStringArray("event_message");
            Console.WriteLine();
            Console.WriteLine($"{kind.Length} events:");
            for (int i = 0; i < kind.Length; i++)
                Console.WriteLine($"  {DateTimeOffset.FromUnixTimeMilliseconds((long)(et[i] * 1000)).ToLocalTime():HH:mm:ss.fff}  {kind[i],-28} {msg[i]}");
        }

        if (csv is not null)
        {
            WriteCsv(npz, csv);
            Console.WriteLine($"\nWrote {csv}");
        }
        return 0;
    }

    private static string ScalarText(NpzReader npz, string name, NpyHeader h) => h.Descr switch
    {
        NpyFormat.Float64Descr => npz.ReadFloat64Array(name)[0].ToString("R", CultureInfo.InvariantCulture),
        NpyFormat.Int64Descr or NpyFormat.Int32Descr => npz.ReadInt64Array(name)[0].ToString(CultureInfo.InvariantCulture),
        _ when NpyFormat.IsUnicode(h.Descr) => "'" + npz.ReadString(name) + "'",
        _ => "?",
    };

    private static void WriteCsv(NpzReader npz, string path)
    {
        string[] names = SampleColumns.Names.Where(npz.Contains).ToArray();
        double[][] cols = names.Select(n =>
            npz.GetHeader(n).Descr == NpyFormat.Float64Descr
                ? npz.ReadFloat64Array(n)
                : npz.ReadInt64Array(n).Select(v => (double)v).ToArray()).ToArray();

        using var w = new StreamWriter(path, false, Encoding.UTF8);
        w.WriteLine(string.Join(',', names));
        int rows = cols.Length == 0 ? 0 : cols.Min(c => c.Length);
        for (int r = 0; r < rows; r++)
            w.WriteLine(string.Join(',', cols.Select(c => c[r].ToString("R", CultureInfo.InvariantCulture))));
    }
}

internal static class RecoverCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: epump recover <output-folder> [--apply]");
            return 2;
        }

        string folder = args[0];
        bool apply = args.Contains("--apply");
        IReadOnlyList<OrphanedRecording> orphans = RecordingFinalizer.FindOrphans(folder);
        if (orphans.Count == 0)
        {
            Console.WriteLine("No interrupted recordings found.");
            return 0;
        }

        int failed = 0;
        foreach (OrphanedRecording o in orphans)
        {
            Console.WriteLine($"{Path.GetFileName(o.SpoolDir)}: {o.Samples} samples -> {o.Manifest.OutputFileName}");
            if (!apply) continue;
            try
            {
                Console.WriteLine($"  saved {RecordingFinalizer.Finalize(o.SpoolDir, folder)}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Console.Error.WriteLine($"  failed: {e.Message}");
                failed++;
            }
        }

        if (!apply) Console.WriteLine("\nPass --apply to write these recordings.");
        return failed == 0 ? 0 : 1;
    }
}
