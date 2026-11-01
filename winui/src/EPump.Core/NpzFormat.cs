using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace EPump.Core;

/// <summary>
/// Writes a NumPy <c>.npz</c> archive: an uncompressed (stored) ZIP whose members are
/// <c>.npy</c> files, one per named array -- exactly what <c>numpy.savez</c> produces, and
/// the same container the NI6451 application writes. A pump recording is a few hundred
/// kilobytes an hour, so unlike NI6451 every array is written straight from memory.
/// </summary>
public sealed class NpzWriter : IDisposable
{
    private readonly FileStream _file;
    private readonly ZipArchive _zip;
    private bool _disposed;

    public NpzWriter(string path)
    {
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        _zip = new ZipArchive(_file, ZipArchiveMode.Create, leaveOpen: false);
    }

    public void AddFloat64Array(string name, ReadOnlySpan<double> values)
    {
        using Stream entry = CreateEntry(name);
        NpyFormat.WriteHeader(entry, NpyFormat.Float64Descr, values.Length);
        entry.Write(MemoryMarshal.AsBytes(values));
    }

    public void AddInt64Array(string name, ReadOnlySpan<long> values)
    {
        using Stream entry = CreateEntry(name);
        NpyFormat.WriteHeader(entry, NpyFormat.Int64Descr, values.Length);
        entry.Write(MemoryMarshal.AsBytes(values));
    }

    /// <summary>A 0-D <c>float64</c> array, the shape <c>numpy.array(1.5)</c> has.</summary>
    public void AddFloat64Scalar(string name, double value)
    {
        using Stream entry = CreateEntry(name);
        NpyFormat.WriteHeader(entry, NpyFormat.Float64Descr);
        Span<double> one = [value];
        entry.Write(MemoryMarshal.AsBytes(one));
    }

    /// <summary>A 0-D <c>int64</c> array, the shape <c>numpy.array(5)</c> has.</summary>
    public void AddInt64Scalar(string name, long value)
    {
        using Stream entry = CreateEntry(name);
        NpyFormat.WriteHeader(entry, NpyFormat.Int64Descr);
        Span<long> one = [value];
        entry.Write(MemoryMarshal.AsBytes(one));
    }

    /// <summary>A 0-D text array: <c>str(npz["firmware"])</c> gives the string back.</summary>
    public void AddString(string name, string value) => WriteStrings(name, [value], scalar: true);

    /// <summary>A 1-D fixed-width text array, padded to its longest element. Loads without <c>allow_pickle</c>.</summary>
    public void AddStringArray(string name, IReadOnlyList<string> values) => WriteStrings(name, values, scalar: false);

    private void WriteStrings(string name, IReadOnlyList<string> values, bool scalar)
    {
        int width = Math.Max(1, values.Count == 0 ? 1 : values.Max(v => v.EnumerateRunes().Count()));
        string descr = NpyFormat.UnicodeDescr(width);

        using Stream entry = CreateEntry(name);
        if (scalar) NpyFormat.WriteHeader(entry, descr);
        else NpyFormat.WriteHeader(entry, descr, values.Count);

        var cell = new byte[width * 4];
        foreach (string v in values)
        {
            Array.Clear(cell);
            Encoding.UTF32.GetBytes(v, 0, v.Length, cell, 0);   // UTF-32LE, NUL-padded, as NumPy stores '<U'
            entry.Write(cell);
        }
    }

    private Stream CreateEntry(string name) =>
        _zip.CreateEntry(name + ".npy", CompressionLevel.NoCompression).Open();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _zip.Dispose();   // also closes _file (leaveOpen: false)
    }
}

/// <summary>Reads back the archives this application writes. Used by the CLI and the self-test.</summary>
public sealed class NpzReader : IDisposable
{
    private readonly ZipArchive _zip;

    public NpzReader(string path) => _zip = ZipFile.OpenRead(path);

    /// <summary>Array names in the archive, without the <c>.npy</c> suffix.</summary>
    public IEnumerable<string> Names => _zip.Entries
        .Where(e => e.Name.EndsWith(".npy", StringComparison.OrdinalIgnoreCase))
        .Select(e => e.Name[..^4]);

    public bool Contains(string name) => _zip.GetEntry(name + ".npy") is not null;

    public NpyHeader GetHeader(string name)
    {
        using Stream s = OpenArray(name);
        return NpyFormat.ReadHeader(s);
    }

    public double[] ReadFloat64Array(string name)
    {
        using Stream s = OpenArray(name);
        NpyHeader h = Expect(name, NpyFormat.ReadHeader(s), NpyFormat.Float64Descr);
        var result = new double[h.Count];
        s.ReadExactly(MemoryMarshal.AsBytes(result.AsSpan()));
        return result;
    }

    /// <summary>Read a 0-D or 1-D integer array, widening <c>&lt;i4</c> to <see cref="long"/>.</summary>
    public long[] ReadInt64Array(string name)
    {
        using Stream s = OpenArray(name);
        NpyHeader h = NpyFormat.ReadHeader(s);
        var result = new long[h.Count];
        switch (h.Descr)
        {
            case NpyFormat.Int64Descr:
                s.ReadExactly(MemoryMarshal.AsBytes(result.AsSpan()));
                break;
            case NpyFormat.Int32Descr:
                var narrow = new int[h.Count];
                s.ReadExactly(MemoryMarshal.AsBytes(narrow.AsSpan()));
                for (long i = 0; i < h.Count; i++) result[i] = narrow[i];
                break;
            default:
                throw new NotSupportedException($"Array '{name}' has dtype '{h.Descr}', expected an integer dtype.");
        }
        return result;
    }

    public string[] ReadStringArray(string name)
    {
        using Stream s = OpenArray(name);
        NpyHeader h = NpyFormat.ReadHeader(s);
        if (!NpyFormat.IsUnicode(h.Descr))
            throw new NotSupportedException($"Array '{name}' has dtype '{h.Descr}', expected a unicode dtype.");

        var cell = new byte[h.ItemSize];
        var result = new string[h.Count];
        for (long i = 0; i < h.Count; i++)
        {
            s.ReadExactly(cell);
            result[i] = Encoding.UTF32.GetString(cell).TrimEnd('\0');
        }
        return result;
    }

    public string ReadString(string name) => ReadStringArray(name) is [var only] ? only : throw new InvalidDataException($"'{name}' is not a single string.");

    private static NpyHeader Expect(string name, NpyHeader h, string descr) =>
        h.Descr == descr ? h : throw new NotSupportedException($"Array '{name}' has dtype '{h.Descr}', expected '{descr}'.");

    private Stream OpenArray(string name)
    {
        ZipArchiveEntry entry = _zip.GetEntry(name + ".npy")
            ?? throw new FileNotFoundException($"Array '{name}' is not present in the archive.");
        return entry.Open();
    }

    public void Dispose() => _zip.Dispose();
}
