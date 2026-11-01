using System.Globalization;

namespace EPump.Core;

/// <summary>Pressure units a ReaXus pump can be configured to report in.</summary>
public enum PressureUnit { Psi, Bar, MPa }

/// <summary>A reply that is not what the command should have produced.</summary>
public sealed class PumpProtocolException(string message) : Exception(message);

/// <summary>Run/stop state, set points and limits as reported by <c>CS</c>.</summary>
/// <param name="FlowSetPoint">mL/min. CP pumps report the flow set point (FI), not the live flow.</param>
public readonly record struct PumpStatus(
    double FlowSetPoint, double UpperLimit, double LowerLimit, PressureUnit Units, bool Running);

/// <summary>Fault indicators as reported by <c>RF</c>.</summary>
public readonly record struct PumpFaults(bool MotorStall, bool OverPressure, bool UnderPressure)
{
    public bool Any => MotorStall || OverPressure || UnderPressure;
}

/// <summary>
/// The ASCII command set of the Teledyne SSI "Next Generation" pumps (ReaXus M1/MX/LS/LD/CP),
/// from SSI document 903068 rev F, "SSI Pump Communication: RS232, USB &amp; Ethernet".
/// Only formatting and parsing live here; the timing rules are <see cref="PumpClient"/>'s.
///
/// Every successful reply starts with <c>OK</c> and ends with <c>/</c>; an unrecognised or
/// mistimed command gets <c>Er/</c>. Commands are not case sensitive and may be terminated
/// with a carriage return, which makes the pump answer immediately instead of waiting to see
/// whether more digits are coming.
/// </summary>
public static class PumpProtocol
{
    public const char EndOfReply = '/';
    public const string Terminator = "\r";
    public const string ErrorReply = "Er/";

    public const string Run = "RU";
    public const string Stop = "ST";
    public const string ClearFaults = "CF";
    public const string CurrentConditions = "CC";
    public const string CurrentStatus = "CS";
    public const string ReadFaults = "RF";
    public const string Identify = "ID";
    public const string MaxFlow = "MF";
    public const string MaxPressure = "MP";
    public const string PressureUnits = "PU";
    public const string UpperLimit = "UP";
    public const string LowerLimit = "LP";
    public const string PressureSetPoint = "PS";
    public const string LeakStatus = "LS";
    public const string KeypadDisable = "KD";
    public const string KeypadEnable = "KE";

    /// <summary>Clears the pump's receive buffer. The only command with no reply at all.</summary>
    public const string ClearBuffer = "#";

    public const double PsiPerBar = 14.503773773;
    public const double PsiPerMPa = 145.03773773;

    // ---------- commands with arguments ----------

    /// <summary>
    /// <c>FIxxxxx</c>: the flow, as an integer count of the pump's flow resolution. On a
    /// 12 mL/min pump (<c>MF</c> reports three decimals) <c>FI12000</c> is 12.000 mL/min.
    /// </summary>
    public static string SetFlow(double mlPerMin, int decimals)
    {
        long counts = (long)Math.Round(mlPerMin * Math.Pow(10, decimals), MidpointRounding.AwayFromZero);
        return "FI" + Math.Clamp(counts, 0, 99_999).ToString("D5", CultureInfo.InvariantCulture);
    }

    public static string SetUpperLimit(double psi, PressureUnit unit) => "UP" + EncodePressure(psi, unit);

    public static string SetLowerLimit(double psi, PressureUnit unit) => "LP" + EncodePressure(psi, unit);

    public static string SetPressureSetPoint(double psi, PressureUnit unit) => "PS" + EncodePressure(psi, unit);

    /// <summary>
    /// Pressure arguments are integers in the pump's own units with a fixed implied scale:
    /// <c>LP200</c> is 200 psi, 20.0 bar or 2.00 MPa. Values are rounded down so that a limit
    /// never ends up above what was asked for.
    /// </summary>
    public static string EncodePressure(double psi, PressureUnit unit)
    {
        double scaled = unit switch
        {
            PressureUnit.Bar => psi / PsiPerBar * 10,
            PressureUnit.MPa => psi / PsiPerMPa * 100,
            _ => psi,
        };
        long counts = (long)Math.Floor(scaled + 1e-9);
        return Math.Clamp(counts, 0, 99_999).ToString("D5", CultureInfo.InvariantCulture);
    }

    /// <summary>A pressure the pump reported, in its configured units, converted to psi.</summary>
    public static double ToPsi(double value, PressureUnit unit) => unit switch
    {
        PressureUnit.Bar => value * PsiPerBar,
        PressureUnit.MPa => value * PsiPerMPa,
        _ => value,
    };

    // ---------- replies ----------

    public static bool IsOk(string reply) =>
        reply.StartsWith("OK", StringComparison.OrdinalIgnoreCase)
        || reply.EndsWith(":OK/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The part of an <c>OK…/</c> reply between the <c>OK</c> and the end marker, with the leading comma dropped.</summary>
    public static string Payload(string reply)
    {
        if (!reply.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            throw new PumpProtocolException($"Expected an OK reply, got '{reply}'.");

        string body = reply[2..];
        if (body.EndsWith(EndOfReply)) body = body[..^1];
        if (body.StartsWith(',')) body = body[1..];
        return body;
    }

    /// <summary><c>CC</c> → <c>OK,&lt;pressure&gt;,&lt;flow&gt;/</c>, e.g. <c>OK,0522,12.00/</c>.</summary>
    public static (double Pressure, double Flow) ParseConditions(string reply)
    {
        string[] f = Fields(reply, CurrentConditions, minFields: 2);
        return (Number(f[0], reply), Number(f[1], reply));
    }

    /// <summary>
    /// <c>CS</c> → <c>OK,&lt;flow&gt;,&lt;UPL&gt;,&lt;LPL&gt;,&lt;units&gt;,0,&lt;R/S&gt;,0/</c>,
    /// e.g. <c>OK,12.00,10000,0000,psi,0,0,0/</c>.
    /// </summary>
    public static PumpStatus ParseStatus(string reply)
    {
        string[] f = Fields(reply, CurrentStatus, minFields: 6);
        return new PumpStatus(
            Number(f[0], reply), Number(f[1], reply), Number(f[2], reply),
            ParseUnitText(f[3], reply), Flag(f[5], reply));
    }

    /// <summary><c>RF</c> → <c>OK,&lt;stall&gt;,&lt;UPF&gt;,&lt;LPF&gt;/</c>.</summary>
    public static PumpFaults ParseFaults(string reply)
    {
        string[] f = Fields(reply, ReadFaults, minFields: 3);
        return new PumpFaults(Flag(f[0], reply), Flag(f[1], reply), Flag(f[2], reply));
    }

    /// <summary><c>MF</c> → <c>OK,MF:12.000/</c>. The number of decimals is the flow resolution FI is scaled by.</summary>
    public static (double MaxFlow, int Decimals) ParseMaxFlow(string reply)
    {
        string value = Tagged(reply, MaxFlow);
        int dot = value.IndexOf('.');
        int decimals = dot < 0 ? 0 : value.Length - dot - 1;
        return (Number(value, reply), decimals);
    }

    /// <summary>Replies of the form <c>OK,XX:&lt;number&gt;/</c>: MP, UP, LP, PS, FI.</summary>
    public static double ParseTaggedNumber(string reply, string tag) => Number(Tagged(reply, tag), reply);

    /// <summary><c>PU</c> → <c>OK,psi/</c>.</summary>
    public static PressureUnit ParseUnits(string reply) => ParseUnitText(Payload(reply), reply);

    /// <summary><c>LS</c> → <c>OK,LS:0/</c>; 1 means a leak is detected.</summary>
    public static bool ParseLeak(string reply) => Flag(Tagged(reply, LeakStatus), reply);

    /// <summary><c>ID</c> → <c>OK, 196000 Version 1.0.0/</c>.</summary>
    public static string ParseIdentity(string reply) => string.Join(' ', Payload(reply).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string[] Fields(string reply, string command, int minFields)
    {
        string[] f = Payload(reply).Split(',', StringSplitOptions.TrimEntries);
        if (f.Length < minFields)
            throw new PumpProtocolException($"{command}: expected at least {minFields} fields, got '{reply}'.");
        return f;
    }

    private static string Tagged(string reply, string tag)
    {
        string body = Payload(reply).Trim();
        string prefix = tag + ":";
        if (!body.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new PumpProtocolException($"Expected '{prefix}…' in '{reply}'.");
        return body[prefix.Length..].Trim();
    }

    private static double Number(string text, string reply) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : throw new PumpProtocolException($"Not a number: '{text}' in '{reply}'.");

    private static bool Flag(string text, string reply) => text.Trim() switch
    {
        "0" => false,
        "1" => true,
        _ => throw new PumpProtocolException($"Expected 0 or 1, got '{text}' in '{reply}'."),
    };

    private static PressureUnit ParseUnitText(string text, string reply) => text.Trim().ToLowerInvariant() switch
    {
        "psi" => PressureUnit.Psi,
        "bar" => PressureUnit.Bar,
        "mpa" => PressureUnit.MPa,
        _ => throw new PumpProtocolException($"Unknown pressure unit '{text}' in '{reply}'."),
    };
}
