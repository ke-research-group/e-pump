using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace EPump.Core;

/// <summary>
/// Minimal writer/reader for the NumPy <c>.npy</c> v1.0 container, limited to the
/// little-endian <c>&lt;f8</c>, <c>&lt;i8</c> and <c>&lt;U</c> (fixed-width UTF-32 text)
/// dtypes and to 0-D or 1-D C-ordered arrays -- which is everything the recordings
/// produced by this application use.
///
/// Copied from the NI6451 application, with text arrays added for the event log. The byte
/// layout is what <c>numpy.load</c> expects, and no array needs <c>allow_pickle</c>.
/// </summary>
public static class NpyFormat
{
    private static readonly byte[] Magic = [0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y'];

    private const int MagicAndVersionLength = 8;   // 6-byte magic + 2 version bytes
    private const int HeaderLengthFieldSize = 2;   // uint16 for v1.0
    private const int ArrayAlign = 64;

    public const string Float64Descr = "<f8";
    public const string Int64Descr = "<i8";

    /// <summary>Only ever read, never written: see <see cref="NpzReader.ReadInt64Array"/>.</summary>
    public const string Int32Descr = "<i4";

    /// <summary>NumPy's fixed-width unicode dtype: <paramref name="chars"/> UTF-32LE code units per element.</summary>
    public static string UnicodeDescr(int chars) => string.Create(CultureInfo.InvariantCulture, $"<U{Math.Max(1, chars)}");

    public static bool IsUnicode(string descr) => descr.StartsWith("<U", StringComparison.Ordinal);

    /// <summary>
    /// Write the <c>.npy</c> prologue. Pass <paramref name="shape"/> as an empty array
    /// for a 0-D scalar array, or a single element for a 1-D array.
    /// </summary>
    public static void WriteHeader(Stream stream, string descr, params long[] shape)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(shape);

        // NumPy emits the header dict with its keys in sorted order and a trailing
        // ", " after every entry; reproducing that exactly keeps the output
        // byte-identical to what np.savez would have written.
        string shapeRepr = shape.Length == 0
            ? "()"
            : shape.Length == 1
                ? string.Create(CultureInfo.InvariantCulture, $"({shape[0]},)")
                : "(" + string.Join(", ", shape.Select(d => d.ToString(CultureInfo.InvariantCulture))) + ")";

        string header = $"{{'descr': '{descr}', 'fortran_order': False, 'shape': {shapeRepr}, }}";

        // Pad with spaces so the data that follows starts on a 64-byte boundary.
        // The +1 accounts for the terminating newline. padLen lands in [1, 64].
        int unpadded = MagicAndVersionLength + HeaderLengthFieldSize + header.Length + 1;
        int padLen = ArrayAlign - (unpadded % ArrayAlign);
        header = header + new string(' ', padLen) + "\n";

        int headerLen = header.Length;
        if (headerLen > ushort.MaxValue)
            throw new InvalidOperationException("Header too long for .npy format version 1.0.");

        stream.Write(Magic);
        stream.WriteByte(1);   // major version
        stream.WriteByte(0);   // minor version

        Span<byte> lenBytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(lenBytes, (ushort)headerLen);
        stream.Write(lenBytes);

        stream.Write(Encoding.ASCII.GetBytes(header));
    }

    /// <summary>Total byte count of the prologue that <see cref="WriteHeader"/> emits.</summary>
    public static int HeaderByteCount(string descr, params long[] shape)
    {
        using var counter = new CountingStream();
        WriteHeader(counter, descr, shape);
        return (int)counter.Length;
    }

    /// <summary>
    /// Read the prologue, leaving <paramref name="stream"/> positioned at the first
    /// data byte. Throws if the array is not a little-endian C-ordered array of a
    /// supported dtype.
    /// </summary>
    public static NpyHeader ReadHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> prologue = stackalloc byte[MagicAndVersionLength];
        stream.ReadExactly(prologue);
        if (!prologue[..6].SequenceEqual(Magic))
            throw new InvalidDataException("Not a .npy file (bad magic).");

        int major = prologue[6];
        int headerLen;
        if (major == 1)
        {
            Span<byte> lenBytes = stackalloc byte[2];
            stream.ReadExactly(lenBytes);
            headerLen = BinaryPrimitives.ReadUInt16LittleEndian(lenBytes);
        }
        else if (major == 2 || major == 3)
        {
            Span<byte> lenBytes = stackalloc byte[4];
            stream.ReadExactly(lenBytes);
            headerLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(lenBytes);
        }
        else
        {
            throw new InvalidDataException($"Unsupported .npy version {major}.");
        }

        byte[] headerBytes = new byte[headerLen];
        stream.ReadExactly(headerBytes);
        string header = Encoding.UTF8.GetString(headerBytes);

        string descr = ExtractQuoted(header, "'descr'")
            ?? throw new InvalidDataException("Missing 'descr' in .npy header.");
        bool fortranOrder = header.Contains("'fortran_order': True", StringComparison.Ordinal);
        if (fortranOrder)
            throw new NotSupportedException("Fortran-ordered .npy arrays are not supported.");

        long[] shape = ExtractShape(header);
        return new NpyHeader(descr, shape);
    }

    private static string? ExtractQuoted(string header, string key)
    {
        int k = header.IndexOf(key, StringComparison.Ordinal);
        if (k < 0) return null;
        int open = header.IndexOf('\'', k + key.Length);
        if (open < 0) return null;
        int close = header.IndexOf('\'', open + 1);
        return close < 0 ? null : header[(open + 1)..close];
    }

    private static long[] ExtractShape(string header)
    {
        int k = header.IndexOf("'shape'", StringComparison.Ordinal);
        if (k < 0) throw new InvalidDataException("Missing 'shape' in .npy header.");
        int open = header.IndexOf('(', k);
        int close = header.IndexOf(')', open + 1);
        if (open < 0 || close < 0) throw new InvalidDataException("Malformed 'shape' in .npy header.");

        string inner = header[(open + 1)..close].Trim();
        if (inner.Length == 0) return [];

        return inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => long.Parse(s, CultureInfo.InvariantCulture))
                    .ToArray();
    }

    private sealed class CountingStream : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
        public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
        public override void WriteByte(byte value) => _length++;
    }
}

/// <summary>Parsed <c>.npy</c> header: dtype string and array shape.</summary>
public readonly record struct NpyHeader(string Descr, long[] Shape)
{
    /// <summary>Total element count (1 for a 0-D scalar array).</summary>
    public long Count => Shape.Length == 0 ? 1 : Shape.Aggregate(1L, (a, b) => a * b);

    public int ItemSize => Descr switch
    {
        NpyFormat.Float64Descr or NpyFormat.Int64Descr => 8,
        NpyFormat.Int32Descr or "<f4" => 4,
        _ when NpyFormat.IsUnicode(Descr) => 4 * int.Parse(Descr.AsSpan(2), CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException($"Unsupported dtype '{Descr}'."),
    };
}
