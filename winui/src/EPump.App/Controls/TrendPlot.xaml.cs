using System.Globalization;
using EPump.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace EPump.App.Controls;

public enum TraceKind { Pressure, Flow }

/// <summary>
/// One live trend -- pressure or flow against wall-clock time -- drawn with Win2D the way the
/// NI6451 application draws its channel plots, so the two windows look alike side by side.
///
/// The x axis is "the last N seconds up to now", the y axis starts at zero. The pressure plot
/// has a fixed top just above the cap, draws the target (PRESS) dashed in the trace colour and
/// HI PR as a red dashed line; the flow plot scales to its max flow, drawn dashed.
/// </summary>
public sealed partial class TrendPlot : UserControl, IDisposable
{
    private const float LeftGutter = 46f;
    private const float BottomGutter = 16f;

    private readonly double[] _t = new double[PumpConfig.MaxPlotPoints + 1];
    private readonly double[] _v = new double[PumpConfig.MaxPlotPoints + 1];
    private readonly CanvasTextFormat _tickFormat = new() { FontSize = 10 };
    private readonly CanvasStrokeStyle _dashed = new() { DashStyle = CanvasDashStyle.Dash };

    private TrendBuffer? _buffer;
    private TraceKind _kind;
    private Palette _palette = Palette.ForTheme(ElementTheme.Light);
    private bool _disposed;

    public TrendPlot()
    {
        try
        {
            InitializeComponent();   // instantiates the Win2D CanvasControl
        }
        catch (Exception e)
        {
            StartupLog.Fatal(
                "TrendPlot could not be constructed. This is where the Win2D CanvasControl is created; "
                + "if Microsoft.Graphics.Canvas.dll is missing or not registered, this is where it shows.", e);
            throw;
        }

        ActualThemeChanged += OnActualThemeChanged;
        Loaded += (_, _) => ApplyTheme();
    }

    public double WindowSeconds { get; set; } = PumpConfig.DefaultPlotWindowSec;

    /// <summary>Pressure: the target (PRESS). Flow: the max flow. Null draws no line.</summary>
    public double? ReferenceValue { get; set; }

    /// <summary>Pressure only: the stop limit (HI PR), drawn in red. Null draws no line.</summary>
    public double? LimitValue { get; set; }

    public void Configure(TrendBuffer buffer, TraceKind kind)
    {
        _buffer = buffer;
        _kind = kind;
    }

    public void Refresh() => PlotCanvas?.Invalidate();

    private void OnActualThemeChanged(FrameworkElement sender, object args) => ApplyTheme();

    private void ApplyTheme()
    {
        _palette = Palette.ForTheme(ActualTheme);
        PlotCanvas.Invalidate();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        CanvasDrawingSession ds = args.DrawingSession;
        Palette p = _palette;
        float width = (float)sender.Size.Width;
        float height = (float)sender.Size.Height;
        if (width <= LeftGutter + 10 || height <= BottomGutter + 10) return;

        ds.Clear(p.Face);

        float left = LeftGutter, right = width - 4, top = 6, bottom = height - BottomGutter;
        double now = UnixTime.Now();
        double since = now - WindowSeconds;

        int n = _buffer?.CopySince(since, _t, _v, PumpConfig.MaxPlotPoints) ?? 0;
        double yMax = YMax(n);

        // Horizontal grid at quarters, labelled.
        for (int i = 0; i <= 4; i++)
        {
            float y = bottom - (bottom - top) * i / 4f;
            ds.DrawLine(left, y, right, y, p.Grid, 1f);
            ds.DrawText(FormatTick(yMax * i / 4), 2, y - 7, p.Label, _tickFormat);
        }

        ds.DrawText("-" + FormatSpan(WindowSeconds), left, bottom + 1, p.Label, _tickFormat);
        ds.DrawText("now", right - 22, bottom + 1, p.Label, _tickFormat);

        float X(double t) => left + (float)((t - since) / WindowSeconds) * (right - left);
        float Y(double v) => Math.Clamp(bottom - (float)(v / yMax) * (bottom - top), top - 1, bottom + 1);

        Color trace = _kind == TraceKind.Pressure ? p.Pressure : p.Flow;
        if (LimitValue is double limit)
        {
            float y = Y(limit);
            ds.DrawLine(left, y, right, y, p.Limit, 1.2f, _dashed);
            ds.DrawText($"HI PR {limit:F0}", right - 76, y - 14, p.Limit, _tickFormat);
        }

        if (ReferenceValue is double reference)
        {
            float y = Y(reference);
            ds.DrawLine(left, y, right, y, trace, 1.2f, _dashed);
            string label = _kind == TraceKind.Pressure ? $"target {reference:F0}" : $"max {reference:0.###}";
            // At the left end, so it cannot collide with the HI PR label when the two lines are close.
            ds.DrawText(label, left + 4, y - 14, trace, _tickFormat);
        }

        if (n < 2) return;

        using var builder = new CanvasPathBuilder(sender);
        builder.BeginFigure(X(_t[0]), Y(_v[0]));
        for (int i = 1; i < n; i++) builder.AddLine(X(_t[i]), Y(_v[i]));
        builder.EndFigure(CanvasFigureLoop.Open);

        using CanvasGeometry geometry = CanvasGeometry.CreatePath(builder);
        ds.DrawGeometry(geometry, trace, 1.6f);
    }

    /// <summary>Pressure: fixed, a little above the cap. Flow: room above the larger of the set point and the data.</summary>
    private double YMax(int n)
    {
        if (_kind == TraceKind.Pressure) return Math.Ceiling(PumpConfig.PressureCapPsi * 1.1 / 100) * 100;

        double max = ReferenceValue ?? 0;
        for (int i = 0; i < n; i++) max = Math.Max(max, _v[i]);
        return max <= 0 ? 1 : NiceCeiling(max * 1.25);
    }

    private static double NiceCeiling(double v)
    {
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(v)));
        foreach (double step in new[] { 1, 2, 2.5, 5, 10 })
            if (step * magnitude >= v) return step * magnitude;
        return 10 * magnitude;
    }

    private static string FormatTick(double v) => v >= 100
        ? v.ToString("0", CultureInfo.InvariantCulture)
        : v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatSpan(double seconds) => seconds >= 60
        ? $"{seconds / 60:0} min"
        : $"{seconds:0} s";

    /// <summary>Win2D draws with raw colours, so the theme is resolved into concrete values here, from <see cref="PumpConfig"/>.</summary>
    private readonly record struct Palette(Color Face, Color Grid, Color Label, Color Pressure, Color Flow, Color Limit)
    {
        private static readonly Palette Light = new(
            Hex(PumpConfig.PlotFaceColor), Hex(PumpConfig.PlotGridColor), Hex(PumpConfig.PlotLabelColor),
            Hex(PumpConfig.PressureTraceColor), Hex(PumpConfig.FlowTraceColor), Hex(PumpConfig.LimitLineColor));

        private static readonly Palette Dark = new(
            Hex(PumpConfig.PlotFaceColorDark), Hex(PumpConfig.PlotGridColorDark), Hex(PumpConfig.PlotLabelColorDark),
            Hex(PumpConfig.PressureTraceColorDark), Hex(PumpConfig.FlowTraceColorDark), Hex(PumpConfig.LimitLineColorDark));

        public static Palette ForTheme(ElementTheme theme) => theme == ElementTheme.Dark ? Dark : Light;

        private static Color Hex(string hex)
        {
            ReadOnlySpan<char> s = hex.AsSpan(hex.StartsWith('#') ? 1 : 0);
            return Color.FromArgb(
                255,
                byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(s.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(s.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Win2D holds unmanaged Direct2D resources that the XAML teardown does not release.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ActualThemeChanged -= OnActualThemeChanged;
        _tickFormat.Dispose();
        _dashed.Dispose();
        PlotCanvas.RemoveFromVisualTree();
    }
}
