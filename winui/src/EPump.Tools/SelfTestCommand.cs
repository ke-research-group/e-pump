using System.Text;
using EPump.Core;

namespace EPump.Tools;

/// <summary>
/// Platform-independent checks over everything that does not need the real pump: the
/// command formatting and reply parsing against the examples in the SSI guide, the safety
/// rules, a full session against the simulator, the <c>.npz</c> layout, crash recovery and
/// the NI6451 naming rule. Runs in CI on every push, and on a Mac.
/// </summary>
internal static class SelfTestCommand
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> RunAsync()
    {
        TestCommandFormatting();
        TestReplyParsing();
        TestSafetyRules();
        TestShearStress();
        TestNpyLayout();
        TestRecordingRoundTrip();
        TestCrashRecovery();
        TestSharedNaming();
        TestTrendBuffer();
        TestUserSettings();
        await TestSessionAgainstSimulator();

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    // ---------- protocol ----------

    private static void TestCommandFormatting()
    {
        Check("FI: 5 mL/min on a 3-decimal pump", PumpProtocol.SetFlow(5, 3) == "FI05000");
        Check("FI: 12.000 mL/min", PumpProtocol.SetFlow(12, 3) == "FI12000");
        Check("FI: 1.23 on a 2-decimal pump", PumpProtocol.SetFlow(1.23, 2) == "FI00123");
        Check("FI: rounds to the nearest count", PumpProtocol.SetFlow(0.0004, 3) == "FI00000" && PumpProtocol.SetFlow(0.0006, 3) == "FI00001");
        Check("FI: clamps to five digits", PumpProtocol.SetFlow(1000, 3) == "FI99999");

        Check("UP: psi", PumpProtocol.SetUpperLimit(2700, PressureUnit.Psi) == "UP02700");
        Check("PS: psi", PumpProtocol.SetPressureSetPoint(2800, PressureUnit.Psi) == "PS02800");
        Check("UP: bar is tenths, rounded down (2800 psi = 193.05 bar)", PumpProtocol.SetUpperLimit(2800, PressureUnit.Bar) == "UP01930");
        Check("UP: MPa is hundredths, rounded down (2800 psi = 19.305 MPa)", PumpProtocol.SetUpperLimit(2800, PressureUnit.MPa) == "UP01930");
        Check("pressure encoding never rounds up", PumpProtocol.EncodePressure(2700.9, PressureUnit.Psi) == "02700");
        Check("bar → psi", Math.Abs(PumpProtocol.ToPsi(193.05, PressureUnit.Bar) - 2800) < 0.1);
    }

    private static void TestReplyParsing()
    {
        // Examples verbatim from SSI document 903068 rev F, section 6.
        (double p, double f) = PumpProtocol.ParseConditions("OK,0522,12.00/");
        Check("CC example", p == 522 && f == 12.00);

        PumpStatus cs = PumpProtocol.ParseStatus("OK,12.00,10000,0000,psi,0,0,0/");
        Check("CS example", cs.FlowSetPoint == 12 && cs.UpperLimit == 10000 && cs.LowerLimit == 0 && cs.Units == PressureUnit.Psi && !cs.Running);
        Check("CS run flag", PumpProtocol.ParseStatus("OK,5.000,02700,0000,psi,0,1,0/").Running);

        Check("RF example", !PumpProtocol.ParseFaults("OK,0,0,0/").Any);
        PumpFaults rf = PumpProtocol.ParseFaults("OK,0,1,0/");
        Check("RF upper pressure fault", rf.OverPressure && !rf.MotorStall && !rf.UnderPressure);

        Check("MF with two decimals", PumpProtocol.ParseMaxFlow("OK,MF:5.00/") == (5.0, 2));
        Check("MF with three decimals", PumpProtocol.ParseMaxFlow("OK,MF:12.000/") == (12.0, 3));
        Check("MP", PumpProtocol.ParseTaggedNumber("OK,MP:10000/", "MP") == 10000);
        Check("UP read", PumpProtocol.ParseTaggedNumber("OK,UP:10000/", "UP") == 10000);
        Check("PS read", PumpProtocol.ParseTaggedNumber("OK,PS:05000/", "PS") == 5000);
        Check("FI echo", PumpProtocol.ParseTaggedNumber("OK,FI:01200/", "FI") == 1200);
        Check("PU", PumpProtocol.ParseUnits("OK,psi/") == PressureUnit.Psi);
        Check("LS", !PumpProtocol.ParseLeak("OK,LS:0/") && PumpProtocol.ParseLeak("OK,LS:1/"));
        Check("ID", PumpProtocol.ParseIdentity("OK, 196000 Version 1.0.0/") == "196000 Version 1.0.0");

        Check("Er/ is not OK", !PumpProtocol.IsOk(PumpProtocol.ErrorReply));
        Check("ZS:OK/ is OK", PumpProtocol.IsOk("ZS:OK/"));
        Check("Er/ cannot be parsed as data", Throws<PumpProtocolException>(() => PumpProtocol.ParseConditions("Er/")));
        Check("a short CS is rejected", Throws<PumpProtocolException>(() => PumpProtocol.ParseStatus("OK,12.00,10000/")));
        Check("a bad flag is rejected", Throws<PumpProtocolException>(() => PumpProtocol.ParseFaults("OK,0,2,0/")));
    }

    // ---------- safety ----------

    private static PumpSample Sample(double t, double p, double flow, double set = 5, bool running = true, PumpFaults faults = default) =>
        new(t, p, flow, set, 2700, running, faults, Leak: false);

    private static void TestSafetyRules()
    {
        Check("limit above the cap is refused", Throws<ArgumentOutOfRangeException>(() => new SafetyMonitor(2801)));
        Check("zero limit is refused", Throws<ArgumentOutOfRangeException>(() => new SafetyMonitor(0)));
        Check("limit at the cap is accepted", new SafetyMonitor(PumpConfig.PressureCapPsi).PressureLimitPsi == 2800);

        var m = new SafetyMonitor(2700);
        m.ArmGrace(1000);
        Check("normal running is OK", !m.Evaluate(Sample(1001, 1500, 5)).Tripped);
        Check("pressure at the limit trips", m.Evaluate(Sample(1002, 2700, 5)).Reason == TripReason.OverPressure);
        Check("pressure above the limit trips even during the grace period", m.Evaluate(Sample(1003, 2750, 5)).Reason == TripReason.OverPressure);
        Check("high pressure while stopped does not trip", !m.Evaluate(Sample(1004, 2750, 0, running: false)).Tripped);

        m = new SafetyMonitor(2700);
        m.ArmGrace(1000);
        Check("low flow during the grace period is ignored", !m.Evaluate(Sample(1005, 2000, 1)).Tripped);
        double t0 = 1000 + PumpConfig.FlowCheckGraceSeconds + 1;
        bool tripped = false;
        for (int i = 0; i < PumpConfig.FlowDeviationSamples - 1; i++)
            tripped |= m.Evaluate(Sample(t0 + i, 2600, 4.5)).Tripped;
        Check("low flow needs several consecutive samples", !tripped);
        Check("persistent low flow trips as flow deviation",
            m.Evaluate(Sample(t0 + 10, 2600, 4.5)).Reason == TripReason.FlowDeviation);

        m = new SafetyMonitor(2700);
        m.ArmGrace(0);
        Check("flow within 5% is fine", !m.Evaluate(Sample(t0, 1500, 4.8)).Tripped && !m.Evaluate(Sample(t0 + 1, 1500, 4.8)).Tripped
                                         && !m.Evaluate(Sample(t0 + 2, 1500, 4.8)).Tripped);
        m.Evaluate(Sample(t0 + 3, 1500, 4.0));
        m.Evaluate(Sample(t0 + 4, 1500, 4.0));
        m.Evaluate(Sample(t0 + 5, 1500, 5.0));
        Check("a recovered flow resets the count", !m.Evaluate(Sample(t0 + 6, 1500, 4.0)).Tripped);

        m = new SafetyMonitor(2700);
        var fault = new PumpFaults(false, true, false);
        Check("a pump fault is reported", m.Evaluate(Sample(1, 0, 0, running: false, faults: fault)).Reason == TripReason.PumpFault);
        Check("... once, not on every poll", !m.Evaluate(Sample(2, 0, 0, running: false, faults: fault)).Tripped);
        m.Evaluate(Sample(3, 0, 0, running: false));
        Check("... and again after it has cleared", m.Evaluate(Sample(4, 0, 0, running: false, faults: fault)).Tripped);
    }

    // ---------- shear stress ----------

    private static void TestShearStress()
    {
        // NI6451's formula: P × piston area / (thickness × 0.5 m), piston area 12.67e-3 m² (×3 for 2D).
        double p = 1000;   // psi
        double pa = p * 6894.757293168;
        Check("1D, 10 cm", Math.Abs(UnitConversion.GetShearStress(p, FaultType.OneD, 0.10) - pa * 12.67e-3 / (0.10 * 0.5)) < 1e-6);
        Check("1D, 5 cm is twice 10 cm",
            Math.Abs(UnitConversion.GetShearStress(p, FaultType.OneD, 0.05) - 2 * UnitConversion.GetShearStress(p, FaultType.OneD, 0.10)) < 1e-6);
        Check("2D ignores the thickness: 3 pistons over 50 cm",
            Math.Abs(UnitConversion.GetShearStress(p, FaultType.TwoD, 0.05) - pa * 3 * 12.67e-3 / (0.5 * 0.5)) < 1e-6);
        Check("geometry reports MPa", Math.Abs(new FaultGeometry(FaultType.OneD, 0.10).ShearStressMPa(1000) - 1.7471) < 1e-3);
        Check("2D effective thickness is 50 cm", new FaultGeometry(FaultType.TwoD, 0.05).EffectiveThicknessM == 0.5);
    }

    // ---------- .npy / .npz ----------

    private static void TestNpyLayout()
    {
        using var ms = new MemoryStream();
        NpyFormat.WriteHeader(ms, NpyFormat.Float64Descr, 3);
        byte[] bytes = ms.ToArray();
        Check("npy: magic and version 1.0", bytes[0] == 0x93 && Encoding.ASCII.GetString(bytes, 1, 5) == "NUMPY" && bytes[6] == 1 && bytes[7] == 0);
        Check("npy: data starts on a 64-byte boundary", bytes.Length % 64 == 0);
        Check("npy: header dict matches numpy's repr",
            Encoding.ASCII.GetString(bytes, 10, bytes.Length - 10)
                .StartsWith("{'descr': '<f8', 'fortran_order': False, 'shape': (3,), }", StringComparison.Ordinal));

        using var u = new MemoryStream();
        NpyFormat.WriteHeader(u, NpyFormat.UnicodeDescr(12));
        Check("npy: unicode scalar header",
            Encoding.ASCII.GetString(u.ToArray(), 10, (int)u.Length - 10)
                .StartsWith("{'descr': '<U12', 'fortran_order': False, 'shape': (), }", StringComparison.Ordinal));
    }

    private static void TestRecordingRoundTrip()
    {
        string dir = TempDir();
        try
        {
            var manifest = new RecordingManifest
            {
                ExperimentSerial = "0207", Run = 5, StartTimeUnix = 1_790_000_000.25, StartStamp = "20261101_143000",
                Port = "COM5", PumpIdentity = "196000 Version 1.0.0", MaxFlow = 12, MaxPressurePsi = 18000, AppVersion = "test",
            };

            string spoolDir;
            using (var spool = new RecordingSpool(dir, manifest))
            {
                spoolDir = spool.Directory;
                spool.AppendEvent(new PumpEvent(1_790_000_000.5, EventKinds.PumpRun, "Run at 5 mL/min — limit 2700 psi"));
                for (int i = 0; i < 10; i++)
                    spool.Append(new PumpSample(1_790_000_001 + i * 0.5, 100 * i, 5, 5, 2700, i < 8, new PumpFaults(false, i >= 8, false), false));
                spool.AppendEvent(new PumpEvent(1_790_000_006, EventKinds.PumpFault, "upper pressure limit exceeded"));
            }

            Check("spool folder is hidden-prefixed in the output folder",
                Path.GetFileName(spoolDir).StartsWith(RecordingSpool.DirPrefix, StringComparison.Ordinal) && Path.GetDirectoryName(spoolDir) == dir);

            string outPath = RecordingFinalizer.Finalize(spoolDir, dir);
            Check("file name follows T{serial}-pump-run{RN}-{start}.npz", Path.GetFileName(outPath) == "T0207-pump-run5-20261101_143000.npz");
            Check("spool removed after a successful save", !Directory.Exists(spoolDir));

            using var npz = new NpzReader(outPath);
            double[] t = npz.ReadFloat64Array("t_unix");
            Check("all samples saved", t.Length == 10 && t[0] == 1_790_000_001 && t[9] == 1_790_000_005.5);
            Check("pressure saved", npz.ReadFloat64Array("pressure_psi")[9] == 900);
            Check("shear stress saved with the recording's geometry",
                Math.Abs(npz.ReadFloat64Array("shear_stress_mpa")[9] - new FaultGeometry(FaultType.OneD, 0.10).ShearStressMPa(900)) < 1e-9);
            Check("geometry recorded", npz.ReadString("fault_type") == "1D" && npz.ReadFloat64Array("fault_thickness_m") is [0.1]);
            Check("flags are int64", npz.GetHeader("running").Descr == NpyFormat.Int64Descr);
            long[] running = npz.ReadInt64Array("running");
            long[] over = npz.ReadInt64Array("fault_over_pressure");
            Check("flag values", running[7] == 1 && running[8] == 0 && over[8] == 1 && over[7] == 0);
            Check("events saved in order", npz.ReadStringArray("event_kind") is [EventKinds.PumpRun, EventKinds.PumpFault]);
            Check("non-ASCII event text survives", npz.ReadStringArray("event_message")[0] == "Run at 5 mL/min — limit 2700 psi");
            Check("start time scalar", npz.ReadFloat64Array("start_time_unix") is [1_790_000_000.25] && npz.GetHeader("start_time_unix").Shape.Length == 0);
            Check("serial and run", npz.ReadString("experiment_serial") == "0207" && npz.ReadInt64Array("run") is [5]);
            Check("pressure cap recorded", npz.ReadFloat64Array("pressure_cap_psi") is [2800.0]);

            var second = new RecordingSpool(dir, manifest);
            second.Close();
            string again = RecordingFinalizer.Finalize(second.Directory, dir);
            Check("an existing file is never overwritten", Path.GetFileName(again) == "T0207-pump-run5-20261101_143000-2.npz");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private static void TestCrashRecovery()
    {
        string dir = TempDir();
        try
        {
            var manifest = new RecordingManifest { ExperimentSerial = "0311", Run = 9, StartStamp = "20261102_090000" };
            var spool = new RecordingSpool(dir, manifest);
            for (int i = 0; i < 4; i++) spool.Append(new PumpSample(i, i, 1, 1, 2700, true, default, false));
            spool.AppendEvent(new PumpEvent(0, EventKinds.PumpRun, "run"));
            spool.Close();

            // A crash mid-row: a few bytes of a fifth sample, and half an event line.
            File.AppendAllText(Path.Combine(spool.Directory, RecordingSpool.SamplesFile), "xyz");
            File.AppendAllText(Path.Combine(spool.Directory, RecordingSpool.EventsFile), "{\"TimeUnix\":1,\"Ki");

            IReadOnlyList<OrphanedRecording> orphans = RecordingFinalizer.FindOrphans(dir);
            Check("interrupted recording is found", orphans.Count == 1 && orphans[0].Samples == 4);

            string outPath = RecordingFinalizer.Finalize(orphans[0].SpoolDir, dir);
            using var npz = new NpzReader(outPath);
            Check("recovery keeps the whole rows", npz.ReadFloat64Array("t_unix").Length == 4);
            Check("recovery keeps the complete events", npz.ReadStringArray("event_kind").Length == 1);
            Check("nothing left to recover", RecordingFinalizer.FindOrphans(dir).Count == 0);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ---------- naming, buffers, settings ----------

    private static void TestSharedNaming()
    {
        var today = new DateOnly(2026, 11, 1);

        NamingSuggestion none = SharedNaming.Suggest(null, today);
        Check("no NI6451 history: defaults", none.ExperimentSerial == NamingState.DefaultExperimentSerial && none.Run == 1);

        var ni = new NamingState();
        ni.RecordRun("207", 5, today);
        NamingSuggestion same = SharedNaming.Suggest(ni, today);
        Check("NI6451 ran today: same serial and run", same.ExperimentSerial == "0207" && same.Run == 5);

        NamingSuggestion next = SharedNaming.Suggest(ni, today.AddDays(1));
        Check("NI6451 last ran yesterday: its next serial, run 1", next.ExperimentSerial == "0208" && next.Run == 1);
    }

    private static void TestTrendBuffer()
    {
        var b = new TrendBuffer(100);
        for (int i = 0; i < 250; i++) b.Add(i, i * 2);
        Check("trend buffer keeps the newest points", b.Count == 100);

        var t = new double[50];
        var v = new double[50];
        int n = b.CopySince(200, t, v, 50);
        Check("window selects by time", n == 50 && t[0] == 200 && t[n - 1] == 249 && v[n - 1] == 498);

        n = b.CopySince(0, t, v, 30);
        Check("point cap is a true upper bound", n <= 30);
        Check("decimated copy still ends at the newest point", t[n - 1] == 249);
    }

    private static void TestUserSettings()
    {
        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, "settings.json");
            new UserSettings { Port = "COM7", FlowMlMin = 2.5, PressureLimitPsi = 2500 }.Save(path);
            UserSettings back = UserSettings.Load(path);
            Check("settings round-trip", back.Port == "COM7" && back.FlowMlMin == 2.5 && back.PressureLimitPsi == 2500);

            File.WriteAllText(path, File.ReadAllText(path).Replace("2500", "9000"));
            Check("an edited limit above the cap is not trusted", UserSettings.Load(path).PressureLimitPsi == PumpConfig.DefaultPressureLimitPsi);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ---------- the whole session, against the simulator ----------

    private static async Task TestSessionAgainstSimulator()
    {
        Check("connect refuses a limit above the cap",
            await ThrowsAsync<ArgumentOutOfRangeException>(() => PumpSession.ConnectAsync(new SimulatedPump(), 3000)));

        var sim = new SimulatedPump { TimeConstantSec = 0.3 };
        var events = new List<PumpEvent>();
        var trips = new List<SafetyVerdict>();

        await using (PumpSession session = await PumpSession.ConnectAsync(sim, 2700))
        {
            session.EventLogged += e => { lock (events) events.Add(e); };
            session.SafetyTripped += v => { lock (trips) trips.Add(v); };

            Check("connect pins the set point at the cap", sim.PressureSetPoint == PumpConfig.PressureSetPointPsi);
            Check("connect applies the limit", sim.UpperLimit == 2700);
            Check("connect reads the pump's flow resolution", session.Info.FlowDecimals == 3 && session.Info.MaxFlow == 12);

            Check("flow above the pump's maximum is refused",
                await ThrowsAsync<ArgumentOutOfRangeException>(() => session.StartPumpAsync(20)));

            await session.StartPumpAsync(5.0);
            Check("pump runs", sim.Running);
            List<string> sent = sim.ReceivedCommands();
            Check("Run was sent after the settings", sent.LastIndexOf("RU") > sent.LastIndexOf("FI05000") && sent.Contains("FI05000"));

            await WaitFor(() => session.LatestSample is { Running: true, PressurePsi: > 1000 }, 5);
            Check("polling reports the running pump", session.LatestSample is { Running: true, PressurePsi: > 1000 });

            // Someone raises the limit on the keypad: it must be put back.
            sim.Exchange("UP09000", TimeSpan.FromSeconds(1));
            await WaitFor(() => sim.UpperLimit == 2700, 5);
            Check("a limit raised on the keypad is restored", sim.UpperLimit == 2700);

            // A blockage: pressure heads for 6000 psi.
            sim.ResistancePsiPerMlMin = 1200;
            await WaitFor(() => !sim.Running, 8);
            Check("over-pressure stops the pump", !sim.Running);
            await WaitFor(() => trips.Count > 0, 3);
            Check("the stop is reported", trips.Count > 0);
            Check("pressure never far beyond the limit", sim.Pressure < 2700 * 1.1);

            sim.ResistancePsiPerMlMin = 300;
            await session.ClearFaultsAsync();
            await WaitFor(() => session.LatestSample is { Faults.Any: false }, 3);

            // Pull the cable.
            bool lost = false;
            session.LinkStateChanged += (isLost, _) => { if (isLost) lost = true; };
            sim.Unplugged = true;
            await WaitFor(() => lost, 5);
            Check("a dead link is reported", lost && session.IsLinkLost);
            sim.Unplugged = false;
            await WaitFor(() => !session.IsLinkLost, 5);
            Check("the link recovers", !session.IsLinkLost);
        }

        lock (events)
        {
            Check("run and safety stop are in the event log",
                events.Any(e => e.Kind == EventKinds.PumpRun)
                && events.Any(e => e.Kind is EventKinds.SafetyOverPressure or EventKinds.PumpFault));
            Check("the keypad change is in the event log", events.Any(e => e.Kind == EventKinds.LimitRestored));
        }
    }

    // ---------- helpers ----------

    private static async Task WaitFor(Func<bool> condition, double seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(50);
    }

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"epump_selftest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static async Task<bool> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (T) { return true; }
    }

    private static void Check(string name, bool ok)
    {
        if (ok) _passed++;
        else _failed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
    }
}
