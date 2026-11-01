using EPump.Core;

namespace EPump.Tools;

internal static class PortsCommand
{
    public static int Run()
    {
        IReadOnlyList<string> ports = SerialPumpTransport.ListPorts();
        if (ports.Count == 0)
        {
            Console.WriteLine("No serial ports found. Is the pump connected and the Teledyne SSI USB driver installed?");
            return 1;
        }

        foreach (string p in ports) Console.WriteLine(p);
        return 0;
    }
}

/// <summary>
/// The first thing to run against real hardware. Only query commands are sent, so it is safe
/// whatever state the pump is in -- including running.
/// </summary>
internal static class PingCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: epump ping <COMx>");
            return 2;
        }

        try
        {
            using var client = new PumpClient(args[0] == PumpConfig.SimulatorPortName
                ? new SimulatedPump()
                : new SerialPumpTransport(args[0]));

            PumpInfo info = await client.IdentifyAsync();
            Console.WriteLine($"Pump        : {info.Identity}");
            Console.WriteLine($"Max flow    : {info.MaxFlow} mL/min ({info.FlowDecimals} decimals)");
            Console.WriteLine($"Max pressure: {info.MaxPressurePsi:F0} psi");
            Console.WriteLine($"Units       : {info.Units}");

            (double p, double f) = await client.ReadConditionsAsync();
            PumpStatus s = await client.ReadStatusAsync();
            PumpFaults faults = await client.ReadFaultsAsync();
            double ps = await client.ReadPressureSetPointAsync();
            bool? leak = await client.ReadLeakAsync();

            Console.WriteLine($"Pressure    : {p:F0} psi");
            Console.WriteLine($"Flow        : {f} mL/min (set point {s.FlowSetPoint})");
            Console.WriteLine($"Running     : {s.Running}");
            Console.WriteLine($"Limits      : upper {s.UpperLimit:F0} psi, lower {s.LowerLimit:F0} psi");
            Console.WriteLine($"CP set point: {ps:F0} psi");
            Console.WriteLine($"Faults      : {(faults.Any ? SafetyMonitor.DescribeFaults(faults) : "none")}");
            Console.WriteLine($"Leak        : {(leak is null ? "no sensor" : leak.Value ? "LEAK DETECTED" : "dry")}");
            return 0;
        }
        catch (Exception e) when (e is TimeoutException or PumpProtocolException or IOException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Failed: {e.Message}");
            return 1;
        }
    }
}
