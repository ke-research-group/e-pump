using EPump.Tools;

return args.Length == 0 ? Usage() : args[0].ToLowerInvariant() switch
{
    "ports" => PortsCommand.Run(),
    "ping" => await PingCommand.RunAsync(args[1..]),
    "dump" => DumpCommand.Run(args[1..]),
    "recover" => RecoverCommand.Run(args[1..]),
    "simulate" => await SimulateCommand.RunAsync(args[1..]),
    "selftest" => await SelfTestCommand.RunAsync(),
    "-h" or "--help" or "help" => Usage(),
    _ => UnknownCommand(args[0]),
};

static int Usage()
{
    Console.WriteLine("""
        epump -- command line companion to the ReaXus CP pump application

        Usage:
          epump ports
              List the serial ports Windows reports. The pump's micro-USB port shows up
              here once the Teledyne SSI driver is installed.

          epump ping <COMx>
              Read-only connection check: identify the pump and print its pressure, flow,
              limits and faults. Sends nothing that changes a setting or starts the pump.

          epump dump <file.npz> [--csv <out.csv>]
              Summarise a recording, and optionally export the samples as CSV.

          epump recover <output-folder> [--apply]
              Find recordings interrupted before their .npz was written. Lists them;
              pass --apply to write the .npz files.

          epump simulate [--out <folder>] [--seconds N] [--flow F]
              Run the built-in simulated pump through the real session and safety code,
              optionally recording to <folder>. No hardware needed.

          epump selftest
              Protocol, safety rules, simulator session, .npz container, recovery and
              naming checks. Runs on any platform, no hardware needed.
        """);
    return 0;
}

static int UnknownCommand(string name)
{
    Console.Error.WriteLine($"Unknown command '{name}'. Run 'epump --help'.");
    return 2;
}
