namespace EPump.Core;

/// <summary>
/// Fixed-capacity ring of (time, value) points for one live trend. Written from the poll
/// thread, read by the UI when it draws; both sides take the lock, and the UI only holds it
/// long enough to copy out the visible window.
/// </summary>
public sealed class TrendBuffer
{
    private readonly object _gate = new();
    private readonly double[] _t;
    private readonly double[] _v;
    private int _start;
    private int _count;

    public TrendBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _t = new double[capacity];
        _v = new double[capacity];
    }

    public int Count { get { lock (_gate) return _count; } }

    public void Add(double t, double value)
    {
        lock (_gate)
        {
            int end = (_start + _count) % _t.Length;
            _t[end] = t;
            _v[end] = value;
            if (_count < _t.Length) _count++;
            else _start = (_start + 1) % _t.Length;
        }
    }

    public void Clear()
    {
        lock (_gate) { _start = 0; _count = 0; }
    }

    /// <summary>
    /// Copy the points with <c>t ≥ since</c> into the destination arrays, keeping at most
    /// <paramref name="maxPoints"/> by taking every k-th point (the stride rounds up, so the cap
    /// is a true upper bound). Returns the number copied.
    /// </summary>
    public int CopySince(double since, double[] times, double[] values, int maxPoints)
    {
        lock (_gate)
        {
            // Points are appended in time order, so the first visible one can be found by bisection.
            int lo = 0, hi = _count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (_t[(_start + mid) % _t.Length] < since) lo = mid + 1;
                else hi = mid;
            }

            int visible = _count - lo;
            int cap = Math.Min(maxPoints, Math.Min(times.Length, values.Length));
            if (visible <= 0 || cap <= 0) return 0;

            int stride = (visible + cap - 1) / cap;
            int n = 0;
            for (int i = lo; i < _count && n < cap; i += stride)
            {
                int k = (_start + i) % _t.Length;
                times[n] = _t[k];
                values[n] = _v[k];
                n++;
            }

            // Always end on the newest point, so the trace reaches "now".
            int last = (_start + _count - 1) % _t.Length;
            if (n > 0 && times[n - 1] != _t[last])
            {
                if (n == cap) n--;
                times[n] = _t[last];
                values[n] = _v[last];
                n++;
            }
            return n;
        }
    }
}
