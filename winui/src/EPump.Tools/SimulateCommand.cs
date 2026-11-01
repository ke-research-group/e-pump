using System.Globalization;
using EPump.Core;

namespace EPump.Tools;

/// <summary>
/// Drives the simulated pump through the same <see cref="PumpSession"/>, safety rule and
/// recorder the application uses, in three phases: below the target the pump runs at its max
/// flow; a stiffer system makes it throttle to hold the target; then PRESS is "raised on the
/// keypad" past the cap, and the pump must be stopped at 2800 psi. With <c>--out</c> it leaves a
/// real <c>.npz</c> behind, which CI loads with NumPy to prove the format.
/// </summary>
internal static class SimulateCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? outDir = null;
        double seconds = 12;
        double flow = 5.0;
        double target = 2000;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
                case "--seconds" when i + 1 < args.Length: seconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--flow" when i + 1 < args.Length: flow = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--target" when i + 1 < args.Length: target = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
            }
        }

        var sim = new SimulatedPump { TimeConstantSec = 1.0 };
        await using PumpSession session = await PumpSession.ConnectAsync(sim);
        Console.WriteLine(session.ConnectionSummary);

        session.SampleReceived += s => Console.WriteLine(
            $"{s.PressurePsi,6:F0} psi  {s.Flow,7:F3} mL/min  {(s.Running ? "RUN " : "stop")}{(s.Faults.Any ? "  FAULT" : string.Empty)}");
        session.EventLogged += e => Console.WriteLine($"  [{e.Kind}] {e.Message}");

        PumpRecorder? recorder = null;
        if (outDir is not null)
        {
            Directory.CreateDirectory(outDir);
            recorder = PumpRecorder.Start(session, outDir, "0001", 1, "simulate", new FaultGeometry(FaultType.OneD, 0.10));
        }

        await session.StartPumpAsync(target, flow);
        await Task.Delay(TimeSpan.FromSeconds(seconds / 3));

        Console.WriteLine("  -- stiffer system: the pump throttles to hold the target --");
        sim.ResistancePsiPerMlMin *= 4;
        await Task.Delay(TimeSpan.FromSeconds(seconds / 3));

        Console.WriteLine("  -- PRESS raised past the cap on the keypad --");
        sim.Exchange("PS05000", TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromSeconds(seconds / 3));

        if (sim.Running) await session.StopPumpAsync();

        if (recorder is not null)
        {
            string path = RecordingFinalizer.Finalize(recorder.Stop(), outDir!);
            Console.WriteLine($"Saved {path}");
        }
        return 0;
    }
}
