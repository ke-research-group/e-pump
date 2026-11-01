using System.IO.Ports;
using System.Text;

namespace EPump.Core;

/// <summary>
/// One byte pipe to a pump. Implementations are synchronous and not thread-safe:
/// <see cref="PumpClient"/> serialises every call and enforces the protocol's timing.
/// </summary>
public interface IPumpTransport : IDisposable
{
    /// <summary>Human-readable name of the link, e.g. <c>COM5</c>.</summary>
    string Name { get; }

    /// <summary>Send one command and return the reply up to and including its <c>/</c>.</summary>
    /// <exception cref="TimeoutException">No complete reply within <paramref name="timeout"/>.</exception>
    string Exchange(string command, TimeSpan timeout);

    /// <summary>Send a command that has no reply (only <c>#</c>).</summary>
    void SendOnly(string command);
}

/// <summary>
/// RS-232, or the micro-USB port once the Teledyne SSI driver has turned it into a virtual
/// COM port -- both look the same from here: 9600 baud, 8 data bits, no parity, 1 stop bit.
/// </summary>
public sealed class SerialPumpTransport : IPumpTransport
{
    private readonly SerialPort _port;
    private readonly StringBuilder _reply = new(64);

    public SerialPumpTransport(string portName)
    {
        _port = new SerialPort(portName, PumpConfig.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            Encoding = Encoding.ASCII,
            // The pump does not monitor DTR, but some USB-serial bridges only pass data once
            // the host has raised it.
            DtrEnable = true,
            RtsEnable = true,
            WriteTimeout = PumpConfig.ResponseTimeoutMs,
        };
        _port.Open();
    }

    public string Name => _port.PortName;

    /// <summary>The COM ports Windows currently reports, sorted numerically (COM3 before COM10).</summary>
    public static IReadOnlyList<string> ListPorts() =>
        SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.Length)
            .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public string Exchange(string command, TimeSpan timeout)
    {
        // A late reply to an earlier, timed-out command must not be read as this one's.
        _port.DiscardInBuffer();
        _port.Write(command + PumpProtocol.Terminator);

        _reply.Clear();
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            int remainingMs = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (remainingMs <= 0)
                throw new TimeoutException($"No reply to '{command}' within {timeout.TotalMilliseconds:F0} ms (got '{_reply}').");

            _port.ReadTimeout = remainingMs;
            int c;
            try { c = _port.ReadChar(); }
            catch (TimeoutException)
            {
                throw new TimeoutException($"No reply to '{command}' within {timeout.TotalMilliseconds:F0} ms (got '{_reply}').");
            }

            // Stray line endings are not part of the protocol; drop them rather than let them
            // end up at the front of a reply.
            if (c is '\r' or '\n') continue;

            _reply.Append((char)c);
            if (c == PumpProtocol.EndOfReply) return _reply.ToString();
            if (_reply.Length > 256)
                throw new PumpProtocolException($"Reply to '{command}' never ended: '{_reply}'.");
        }
    }

    public void SendOnly(string command) => _port.Write(command);

    public void Dispose()
    {
        try { if (_port.IsOpen) _port.Close(); }
        catch (IOException) { /* the USB device may already be gone */ }
        _port.Dispose();
    }
}
