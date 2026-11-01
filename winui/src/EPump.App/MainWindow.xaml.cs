using System.Globalization;
using EPump.App.Controls;
using EPump.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Globalization.NumberFormatting;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace EPump.App;

/// <summary>
/// Wires the UI to a <see cref="PumpSession"/> and a <see cref="PumpRecorder"/>. Built to sit
/// beside the NI6451 window and behave like it: the same rail-and-readout layout, inline
/// <see cref="InfoBar"/> notifications instead of modal dialogs, and the same run naming.
///
/// Two things are deliberately different. Live monitoring has no button: it starts on Connect
/// and runs until Disconnect, because the software safety checks only exist while the pump is
/// being polled. And the pump's Run/Stop are separate from Record/Stop recording, so a
/// recording can span several runs and a stopped recording never stops the pump.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int UiRefreshMs = 250;
    private const string EmptyReadout = "—";

    private readonly DispatcherTimer _uiTimer = new();
    private readonly TrendBuffer _pressureTrend = new(PumpConfig.TrendCapacity);
    private readonly TrendBuffer _flowTrend = new(PumpConfig.TrendCapacity);
    private readonly UserSettings _settings = UserSettings.Load(UserSettings.DefaultPath);
    private readonly string _appVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private PumpSession? _session;
    private PumpRecorder? _recorder;
    private DateTime _recordStartedUtc;
    private string? _saveDir;
    private IReadOnlyList<OrphanedRecording> _orphans = [];
    private string? _lastAlert;

    /// <summary>True while a command other than Stop is in flight, so the UI cannot interleave Run, Apply and Connect.</summary>
    private bool _busy;

    public MainWindow()
    {
        StartupLog.Write("MainWindow: InitializeComponent");
        InitializeComponent();
        StartupLog.Write("MainWindow: XAML loaded");

        Title = "ReaXus CP Pump Control";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1380, 900));
        AppWindow.Closing += OnClosing;

        FlowBox.Minimum = 0.001;
        FlowBox.Maximum = SimulatedPump.MaxFlowMlMin;
        FlowBox.NumberFormatter = new DecimalFormatter
        {
            FractionDigits = 3,
            NumberRounder = new IncrementNumberRounder { Increment = 0.001, RoundingAlgorithm = RoundingAlgorithm.RoundHalfUp },
        };
        FlowBox.Value = _settings.FlowMlMin;

        LimitBox.Minimum = 1;
        LimitBox.Maximum = PumpConfig.PressureCapPsi;
        LimitBox.Value = _settings.PressureLimitPsi;
        KeypadLockToggle.IsOn = _settings.LockKeypad;

        SafetyCaptionText.Text =
            $"The pump's constant-pressure set point is pinned at {PumpConfig.PressureSetPointPsi:F0} psi, the cap, so it "
            + $"delivers exactly this flow. The limit cannot exceed {PumpConfig.PressureCapPsi:F0} psi. Reaching it stops the "
            + $"pump, and so does the flow dropping more than {PumpConfig.FlowDeviationTolerance:P0} below the set point "
            + $"({PumpConfig.FlowCheckGraceSeconds:F0} s after a start or a flow change).";

        foreach (int s in PumpConfig.PlotWindowsSec) WindowCombo.Items.Add(s >= 60 ? $"{s / 60} min" : $"{s} s");
        WindowCombo.SelectedIndex = Array.IndexOf(PumpConfig.PlotWindowsSec, PumpConfig.DefaultPlotWindowSec);
        PressurePlot.Configure(_pressureTrend, TraceKind.Pressure);
        FlowPlot.Configure(_flowTrend, TraceKind.Flow);

        RnBox.Minimum = 0;
        RnBox.Maximum = 999_999;
        ApplySuggestedNaming();

        _uiTimer.Interval = TimeSpan.FromMilliseconds(UiRefreshMs);
        _uiTimer.Tick += (_, _) => RefreshLive();
        _uiTimer.Start();

        RefreshPorts();
        SetStatus("Not connected", StatusKind.Idle);
        UpdateControls();
        StartupLog.Write("MainWindow: ready");
    }

    // ---------- ports and connection ----------

    private void OnRefreshPorts(object sender, RoutedEventArgs e) => RefreshPorts();

    private void RefreshPorts()
    {
        string? current = PortCombo.SelectedItem as string ?? _settings.Port;
        PortCombo.Items.Clear();

        IReadOnlyList<string> ports;
        try
        {
            ports = SerialPumpTransport.ListPorts();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            ports = [];
            ShowAlert(InfoBarSeverity.Warning, "Could not list serial ports", ex.Message);
        }

        foreach (string p in ports) PortCombo.Items.Add(p);
        PortCombo.Items.Add(PumpConfig.SimulatorPortName);

        PortCombo.SelectedItem = PortCombo.Items.Contains(current) ? current : PortCombo.Items[0];
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (_session is null) await ConnectAsync();
        else await DisconnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (PortCombo.SelectedItem is not string port)
        {
            ShowAlert(InfoBarSeverity.Warning, "No port", "Choose the pump's COM port first.");
            return;
        }

        if (!TryReadLimit(out double limit)) return;

        _busy = true;
        UpdateControls();
        SetStatus($"Connecting to {port}…", StatusKind.Busy);
        try
        {
            IPumpTransport transport = port == PumpConfig.SimulatorPortName
                ? new SimulatedPump()
                : await Task.Run(() => (IPumpTransport)new SerialPumpTransport(port));

            PumpSession session = await PumpSession.ConnectAsync(transport, limit);
            session.SampleReceived += OnSample;
            session.SafetyTripped += OnSafetyTripped;
            session.LinkStateChanged += OnLinkStateChanged;
            _session = session;

            PumpInfo info = session.Info;
            FlowBox.Maximum = info.MaxFlow;
            FlowBox.SmallChange = info.FlowDecimals >= 3 ? 0.1 : 1;
            if (FlowBox.Value > info.MaxFlow) FlowBox.Value = info.MaxFlow;
            PumpInfoText.Text =
                $"{info.Identity} · max {info.MaxFlow.ToString("F" + info.FlowDecimals, CultureInfo.InvariantCulture)} mL/min, "
                + $"{info.MaxPressurePsi:F0} psi · reports in {info.Units}";
            if (session.FlowSetPoint > 0 && session.LatestSample is null) FlowBox.Value = session.FlowSetPoint;

            if (KeypadLockToggle.IsOn) await TrySetKeypadLockAsync(true);

            _pressureTrend.Clear();
            _flowTrend.Clear();
            _settings.Port = port;
            _settings.Save(UserSettings.DefaultPath);
            ClearAlert();
            SetStatus($"Connected to {port} · limit {limit:F0} psi · set point pinned at {PumpConfig.PressureSetPointPsi:F0} psi", StatusKind.Connected);
        }
        catch (Exception ex) when (IsPumpError(ex) || ex is ArgumentException)
        {
            SetStatus("Not connected", StatusKind.Error);
            ShowAlert(InfoBarSeverity.Error, $"Could not connect to {port}", ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateControls();
        }
    }

    private async Task DisconnectAsync()
    {
        if (_session is null) return;
        if (_session.LatestSample is { Running: true })
        {
            ShowAlert(InfoBarSeverity.Warning, "The pump is running",
                "Stop the pump before disconnecting. Once disconnected, nothing in this application can stop it.");
            return;
        }

        _busy = true;
        UpdateControls();
        try
        {
            if (_recorder is not null) await StopRecordingAsync();
            if (KeypadLockToggle.IsOn) await TrySetKeypadLockAsync(false);
            await CloseSessionAsync();
            SetStatus("Disconnected", StatusKind.Idle);
        }
        finally
        {
            _busy = false;
            UpdateControls();
        }
    }

    private async Task CloseSessionAsync()
    {
        PumpSession? session = _session;
        if (session is null) return;
        _session = null;
        session.SampleReceived -= OnSample;
        session.SafetyTripped -= OnSafetyTripped;
        session.LinkStateChanged -= OnLinkStateChanged;
        await session.DisposeAsync();
    }

    private async void OnKeypadLockToggled(object sender, RoutedEventArgs e)
    {
        _settings.LockKeypad = KeypadLockToggle.IsOn;
        _settings.Save(UserSettings.DefaultPath);
        if (_session is not null) await TrySetKeypadLockAsync(KeypadLockToggle.IsOn);
    }

    private async Task TrySetKeypadLockAsync(bool locked)
    {
        try { await _session!.SetKeypadLockedAsync(locked); }
        catch (Exception ex) when (IsPumpError(ex))
        {
            ShowAlert(InfoBarSeverity.Warning, "Keypad lock not changed", ex.Message);
        }
    }

    // ---------- pump control ----------

    private async void OnRunPump(object sender, RoutedEventArgs e)
    {
        if (_session is null || !TryReadFlow(out double flow) || !TryReadLimit(out double limit)) return;

        _busy = true;
        UpdateControls();
        SafetyBar.IsOpen = false;
        try
        {
            if (Math.Abs(limit - _session.PressureLimitPsi) > 0.5) await _session.SetPressureLimitAsync(limit);
            await _session.StartPumpAsync(flow);
            RememberFlowAndLimit(flow, limit);
            ClearAlert();
            SetStatus($"Running at {flow} mL/min · limit {limit:F0} psi", StatusKind.Running);
        }
        catch (Exception ex) when (IsPumpError(ex) || ex is ArgumentException)
        {
            ShowAlert(InfoBarSeverity.Error, "The pump was not started", ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateControls();
        }
    }

    /// <summary>Never gated on <see cref="_busy"/>: Stop must work while anything else is in flight.</summary>
    private async void OnStopPump(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        try
        {
            await _session.StopPumpAsync();
            SetStatus("Pump stopped", StatusKind.Connected);
        }
        catch (Exception ex) when (IsPumpError(ex))
        {
            SetStatus("STOP FAILED", StatusKind.Error);
            ShowAlert(InfoBarSeverity.Error, "STOP DID NOT REACH THE PUMP",
                $"{ex.Message} Stop the pump with the RUN/STOP key on its front panel, or switch it off.");
        }
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_session is null || !TryReadFlow(out double flow) || !TryReadLimit(out double limit)) return;

        _busy = true;
        UpdateControls();
        try
        {
            if (Math.Abs(limit - _session.PressureLimitPsi) > 0.5) await _session.SetPressureLimitAsync(limit);
            if (Math.Abs(flow - _session.FlowSetPoint) > 1e-9) await _session.SetFlowAsync(flow);
            RememberFlowAndLimit(flow, limit);
            ClearAlert();
            SetStatus($"Applied: {flow} mL/min · limit {limit:F0} psi", _session.LatestSample is { Running: true } ? StatusKind.Running : StatusKind.Connected);
        }
        catch (Exception ex) when (IsPumpError(ex) || ex is ArgumentException)
        {
            ShowAlert(InfoBarSeverity.Error, "Settings not applied", ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateControls();
        }
    }

    private async void OnClearFault(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        try
        {
            await _session.ClearFaultsAsync();
            SafetyBar.IsOpen = false;
        }
        catch (Exception ex) when (IsPumpError(ex))
        {
            ShowAlert(InfoBarSeverity.Error, "Fault not cleared", ex.Message);
        }
    }

    private bool TryReadFlow(out double flow)
    {
        flow = FlowBox.Value;
        double max = _session?.Info.MaxFlow ?? FlowBox.Maximum;
        if (double.IsNaN(flow) || flow <= 0 || flow > max)
        {
            ShowAlert(InfoBarSeverity.Warning, "Check the flow", $"Enter a flow above 0 and at most {max} mL/min.");
            return false;
        }
        return true;
    }

    private bool TryReadLimit(out double limit)
    {
        limit = LimitBox.Value;
        if (double.IsNaN(limit) || limit <= 0 || limit > PumpConfig.PressureCapPsi)
        {
            ShowAlert(InfoBarSeverity.Warning, "Check the pressure limit",
                $"Enter a limit above 0 and at most {PumpConfig.PressureCapPsi:F0} psi.");
            return false;
        }
        return true;
    }

    private void RememberFlowAndLimit(double flow, double limit)
    {
        _settings.FlowMlMin = flow;
        _settings.PressureLimitPsi = limit;
        _settings.Save(UserSettings.DefaultPath);
    }

    // ---------- session events (poll thread) ----------

    /// <summary>Poll thread. The trend buffers are thread-safe; everything visual happens on the UI timer.</summary>
    private void OnSample(PumpSample s)
    {
        _pressureTrend.Add(s.TimeUnix, s.PressurePsi);
        _flowTrend.Add(s.TimeUnix, s.Flow);
    }

    private void OnSafetyTripped(SafetyVerdict verdict) => OnUiThread(() =>
    {
        SafetyBar.Title = verdict.Reason switch
        {
            TripReason.OverPressure => "Pump stopped: pressure limit reached",
            TripReason.FlowDeviation => "Pump stopped: flow no longer constant",
            _ => "Pump fault",
        };
        SafetyBar.Message = verdict.Message;
        SafetyBar.IsOpen = true;
        SetStatus(SafetyBar.Title, StatusKind.Error);
        UpdateControls();
    });

    private void OnLinkStateChanged(bool lost, string message) => OnUiThread(() =>
    {
        if (lost)
        {
            bool mayBeRunning = _session?.LatestSample is { Running: true };
            ShowAlert(InfoBarSeverity.Error, "Lost contact with the pump",
                message + (mayBeRunning
                    ? " The pump was running and may still be: the safety checks in this application cannot act until it answers again. Use the pump's front-panel RUN/STOP key if needed."
                    : string.Empty));
            SetStatus("Pump not answering", StatusKind.Error);
        }
        else
        {
            ClearAlert();
            SetStatus("Pump answering again", StatusKind.Connected);
        }
        UpdateControls();
    });

    // ---------- recording ----------

    private async void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        _saveDir = folder.Path;
        OutputFolderText.Text = folder.Path;
        ScanForInterruptedRecordings();
        UpdateControls();
    }

    private void OnRecord(object sender, RoutedEventArgs e)
    {
        if (_session is null || _saveDir is null || _recorder is not null) return;

        try
        {
            _recorder = PumpRecorder.Start(_session, _saveDir, CurrentSerialPadded, CurrentRn, _appVersion);
            _recordStartedUtc = DateTime.UtcNow;
            StatsPanel.Visibility = Visibility.Visible;
            SetStatus($"Recording {Path.GetFileName(_recorder.Spool.Manifest.OutputFileName)}", StatusKind.Recording);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _recorder = null;
            ShowAlert(InfoBarSeverity.Error, "Could not start recording", ex.Message);
        }
        UpdateControls();
    }

    private async void OnStopRecord(object sender, RoutedEventArgs e) => await StopRecordingAsync();

    private async Task StopRecordingAsync()
    {
        PumpRecorder? recorder = _recorder;
        if (recorder is null || _saveDir is null) return;
        _recorder = null;
        StatsPanel.Visibility = Visibility.Collapsed;
        UpdateControls();

        string saveDir = _saveDir;
        try
        {
            string spoolDir = recorder.Stop();
            string outPath = await Task.Run(() => RecordingFinalizer.Finalize(spoolDir, saveDir));
            SetStatus($"Saved {Path.GetFileName(outPath)} · {recorder.Spool.SampleCount:N0} samples",
                _session?.LatestSample is { Running: true } ? StatusKind.Running : StatusKind.Connected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowAlert(InfoBarSeverity.Error, "Save failed",
                $"{ex.Message} The spool files were kept, so the data can be recovered from the output folder.");
        }

        ApplySuggestedNaming();
        ScanForInterruptedRecordings();
    }

    private void ScanForInterruptedRecordings()
    {
        if (_saveDir is null) return;

        // The recording in progress is not an orphan.
        string? active = _recorder?.Spool.Directory;
        _orphans = RecordingFinalizer.FindOrphans(_saveDir).Where(o => o.SpoolDir != active).ToList();
        if (_orphans.Count == 0)
        {
            RecoveryBar.IsOpen = false;
            return;
        }

        RecoveryBar.Message =
            $"{_orphans.Count} recording(s) in this folder were interrupted before being saved "
            + $"({_orphans.Sum(o => o.Samples):N0} samples). They can be saved as normal .npz files.";
        RecoveryBar.IsOpen = true;
    }

    private async void OnRecover(object sender, RoutedEventArgs e)
    {
        if (_saveDir is null || _orphans.Count == 0) return;

        IReadOnlyList<OrphanedRecording> toRecover = _orphans;
        string saveDir = _saveDir;
        RecoveryBar.IsOpen = false;
        try
        {
            List<string> saved = await Task.Run(() => toRecover.Select(o => RecordingFinalizer.Finalize(o.SpoolDir, saveDir)).ToList());
            ShowAlert(InfoBarSeverity.Success, "Recovery complete", string.Join("\n", saved.Select(Path.GetFileName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowAlert(InfoBarSeverity.Error, "Recovery failed", $"{ex.Message} The spool files were left in place; you can retry.");
        }
        ScanForInterruptedRecordings();
    }

    // ---------- naming ----------

    private void OnSyncNaming(object sender, RoutedEventArgs e) => ApplySuggestedNaming();

    /// <summary>Seed the naming from NI6451's run numbering; see <see cref="SharedNaming"/> for the rule.</summary>
    private void ApplySuggestedNaming()
    {
        NamingSuggestion s = SharedNaming.Suggest(DateOnly.FromDateTime(DateTime.Now));
        SerialBox.Text = s.ExperimentSerial;
        RnBox.Value = s.Run;
        NamingHintText.Text = s.Hint;
        UpdateFileNamePreview();
    }

    private void OnNamingChanged(object sender, TextChangedEventArgs e)
    {
        string digits = new(SerialBox.Text.Where(char.IsAsciiDigit).ToArray());
        if (digits != SerialBox.Text)
        {
            int caret = Math.Max(0, SerialBox.SelectionStart - (SerialBox.Text.Length - digits.Length));
            SerialBox.Text = digits;
            SerialBox.SelectionStart = Math.Min(caret, digits.Length);
            return;   // the assignment re-enters this handler
        }
        UpdateFileNamePreview();
    }

    private void OnRunNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue)) sender.Value = 0;
        UpdateFileNamePreview();
    }

    private string CurrentSerialPadded => SerialBox.Text.Trim().PadLeft(4, '0');

    private int CurrentRn => double.IsNaN(RnBox.Value) ? 0 : (int)RnBox.Value;

    private void UpdateFileNamePreview()
    {
        if (FileNamePreviewText is null) return;
        FileNamePreviewText.Text = $"T{CurrentSerialPadded}-pump-run{CurrentRn}-<start time>.npz";
    }

    // ---------- live display ----------

    private void OnWindowChanged(object sender, SelectionChangedEventArgs e)
    {
        int i = WindowCombo.SelectedIndex;
        if (i < 0 || PressurePlot is null) return;
        PressurePlot.WindowSeconds = PumpConfig.PlotWindowsSec[i];
        FlowPlot.WindowSeconds = PumpConfig.PlotWindowsSec[i];
    }

    private void RefreshLive()
    {
        PumpSession? session = _session;
        PumpSample? s = session?.LatestSample;
        int decimals = session?.Info.FlowDecimals ?? 3;

        PressureText.Text = s is null ? EmptyReadout : s.PressurePsi.ToString("F0", CultureInfo.InvariantCulture);
        FlowText.Text = s is null ? EmptyReadout : s.Flow.ToString("F" + decimals, CultureInfo.InvariantCulture);
        SetFlowText.Text = session is null ? EmptyReadout : session.FlowSetPoint.ToString("F" + decimals, CultureInfo.InvariantCulture);
        LimitText.Text = session is null ? EmptyReadout : session.PressureLimitPsi.ToString("F0", CultureInfo.InvariantCulture);

        SetStateTile(session, s);

        PressurePlot.ReferenceValue = session?.PressureLimitPsi;
        FlowPlot.ReferenceValue = session?.FlowSetPoint;
        PressurePlot.Refresh();
        FlowPlot.Refresh();

        if (_recorder is not null)
        {
            TimeSpan t = DateTime.UtcNow - _recordStartedUtc;
            StatElapsed.Text = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
            StatSamples.Text = _recorder.Spool.SampleCount.ToString("N0", CultureInfo.InvariantCulture);
        }

        UpdateControls();
    }

    private void SetStateTile(PumpSession? session, PumpSample? s)
    {
        (string text, string glyph, Brush brush) = (session, s) switch
        {
            (null, _) => ("Offline", "", ThemeBrush("TextFillColorTertiaryBrush", 0x8A, 0x8A, 0x8A)),
            ({ IsLinkLost: true }, _) => ("No reply", "", ThemeBrush("SystemFillColorCriticalBrush", 0xC4, 0x2B, 0x1C)),
            (_, null) => ("Waiting", "", ThemeBrush("TextFillColorTertiaryBrush", 0x8A, 0x8A, 0x8A)),
            (_, { Faults.Any: true }) => ("Fault", "", ThemeBrush("SystemFillColorCriticalBrush", 0xC4, 0x2B, 0x1C)),
            (_, { Running: true }) => ("Running", "", ThemeBrush("SystemFillColorSuccessBrush", 0x0F, 0x7B, 0x0F)),
            _ => ("Stopped", "", ThemeBrush("TextFillColorSecondaryBrush", 0x60, 0x60, 0x60)),
        };

        if (StateText.Text != text) StateText.Text = text;
        StateIcon.Glyph = glyph;
        StateIcon.Foreground = brush;
        StateText.Foreground = brush;
    }

    private void UpdateControls()
    {
        bool connected = _session is not null;
        bool running = _session?.LatestSample is { Running: true };
        bool fault = _session?.LatestSample is { Faults.Any: true };
        bool recording = _recorder is not null;

        PortCombo.IsEnabled = !connected && !_busy;
        RefreshPortsButton.IsEnabled = !connected && !_busy;
        ConnectButton.IsEnabled = !_busy && !running;
        ConnectText.Text = connected ? "Disconnect" : "Connect";
        ConnectIcon.Glyph = connected ? "" : "";

        ApplyButton.IsEnabled = connected && !_busy;
        RunPumpButton.IsEnabled = connected && !_busy && !running && !fault;
        StopPumpButton.IsEnabled = connected;   // always, whatever else is happening
        ClearFaultButton.IsEnabled = connected;

        RecordButton.IsEnabled = connected && _saveDir is not null && !recording;
        StopRecordButton.IsEnabled = recording;
        ChooseFolderButton.IsEnabled = !recording;
        SerialBox.IsEnabled = !recording;
        RnBox.IsEnabled = !recording;
        SyncNamingButton.IsEnabled = !recording;
    }

    // ---------- status and alerts ----------

    private enum StatusKind { Idle, Connected, Running, Recording, Busy, Error }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        StatusDot.Fill = kind switch
        {
            StatusKind.Recording => ThemeBrush("SystemFillColorCriticalBrush", 0xC4, 0x2B, 0x1C),
            StatusKind.Running => ThemeBrush("SystemFillColorSuccessBrush", 0x0F, 0x7B, 0x0F),
            StatusKind.Connected => ThemeBrush("AccentFillColorDefaultBrush", 0x00, 0x5F, 0xB8),
            StatusKind.Busy => ThemeBrush("SystemFillColorCautionBrush", 0x9D, 0x5D, 0x00),
            StatusKind.Error => ThemeBrush("SystemFillColorCriticalBrush", 0xC4, 0x2B, 0x1C),
            _ => ThemeBrush("TextFillColorTertiaryBrush", 0x8A, 0x8A, 0x8A),
        };
    }

    private readonly Dictionary<string, Brush> _brushCache = new();

    /// <summary>Resolve a theme brush by key, falling back to a fixed colour, as in NI6451.</summary>
    private Brush ThemeBrush(string key, byte r, byte g, byte b)
    {
        if (_brushCache.TryGetValue(key, out Brush? cached)) return cached;

        Brush brush;
        try
        {
            brush = Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush themed
                ? themed
                : new SolidColorBrush(Color.FromArgb(255, r, g, b));
        }
        catch (Exception e) when (e is InvalidCastException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            brush = new SolidColorBrush(Color.FromArgb(255, r, g, b));
        }

        _brushCache[key] = brush;
        return brush;
    }

    private void ShowAlert(InfoBarSeverity severity, string title, string message)
    {
        string key = severity + title + message;
        if (key == _lastAlert && AlertBar.IsOpen) return;
        _lastAlert = key;

        AlertBar.Severity = severity;
        AlertBar.Title = title;
        AlertBar.Message = message;
        AlertBar.IsOpen = true;
    }

    private void ClearAlert()
    {
        AlertBar.IsOpen = false;
        _lastAlert = null;
    }

    private static bool IsPumpError(Exception e) =>
        e is TimeoutException or PumpProtocolException or IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentOutOfRangeException;

    private void OnUiThread(Action action)
    {
        if (DispatcherQueue.HasThreadAccess) action();
        else DispatcherQueue.TryEnqueue(() => action());
    }

    // ---------- shutdown ----------

    /// <summary>
    /// A running pump keeps the window open: closing would take the software safety checks
    /// with it while the pump carries on. Otherwise any recording is saved and the keypad unlocked.
    /// </summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_session?.LatestSample is { Running: true })
        {
            args.Cancel = true;
            ShowAlert(InfoBarSeverity.Warning, "The pump is running",
                "Stop the pump before closing. With the window closed, nothing would be watching the pressure.");
            return;
        }

        _uiTimer.Stop();

        if (_recorder is not null && _saveDir is not null)
        {
            try { RecordingFinalizer.Finalize(_recorder.Stop(), _saveDir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                StartupLog.Write("Save on close failed; the spool is kept for recovery: " + e.Message);
            }
            _recorder = null;
        }

        if (_session is not null)
        {
            PumpSession session = _session;
            bool unlockKeypad = KeypadLockToggle.IsOn;
            // Off the UI thread, so nothing the session awaits can need this thread back.
            Task.Run(async () =>
            {
                if (unlockKeypad)
                {
                    try { await session.SetKeypadLockedAsync(false); }
                    catch (Exception e) when (IsPumpError(e)) { }
                }
                await session.DisposeAsync();
            }).Wait(TimeSpan.FromSeconds(5));
            _session = null;
        }

        PressurePlot.Dispose();
        FlowPlot.Dispose();
    }
}
