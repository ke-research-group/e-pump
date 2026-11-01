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
/// Live monitoring has no button: it starts on Connect and runs until Disconnect, because the
/// software safety checks only exist while the pump is being polled. Every run is recorded:
/// Run starts the recording just before the pump, STOP saves it right after the pump stops,
/// and if the pump stops by itself the recording runs on for
/// <see cref="PumpConfig.RecordTailSeconds"/> to catch the pressure decay, then saves.
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

    /// <summary>The pump has been seen running since this recording started, so a stop now means the run is over.</summary>
    private bool _sawRunning;

    /// <summary>Set when the pump stopped by itself: the recording is saved at this time.</summary>
    private DateTime? _tailUntilUtc;
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

        TargetBox.Minimum = 1;
        TargetBox.Maximum = PumpConfig.PressureCapPsi;
        TargetBox.Value = _settings.TargetPressurePsi;
        KeypadLockToggle.IsOn = _settings.LockKeypad;

        // Checking a radio button populates the thickness list, which selects the saved thickness.
        if (_settings.FaultType == "2D") Fault2DRadio.IsChecked = true;
        else Fault1DRadio.IsChecked = true;

        SafetyCaptionText.Text =
            $"The pump varies its flow, up to the max flow, to hold the target pressure (front panel PRESS). "
            + $"HI PR is pinned at {PumpConfig.UpperLimitPsi:F0} psi: the pump stops itself above it, and this application "
            + $"stops it at {PumpConfig.PressureCapPsi:F0} psi too. A target close to {PumpConfig.PressureCapPsi:F0} psi leaves "
            + "no room for overshoot.";

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

        _busy = true;
        UpdateControls();
        SetStatus($"Connecting to {port}…", StatusKind.Busy);
        try
        {
            IPumpTransport transport = port == PumpConfig.SimulatorPortName
                ? new SimulatedPump()
                : await Task.Run(() => (IPumpTransport)new SerialPumpTransport(port));

            PumpSession session = await PumpSession.ConnectAsync(transport);
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

            if (KeypadLockToggle.IsOn) await TrySetKeypadLockAsync(true);

            _pressureTrend.Clear();
            _flowTrend.Clear();
            _settings.Port = port;
            _settings.Save(UserSettings.DefaultPath);
            ClearAlert();
            SetStatus($"Connected to {port} · HI PR pinned at {PumpConfig.UpperLimitPsi:F0} psi", StatusKind.Connected);
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

    /// <summary>
    /// Start the recording first, so it holds the whole run including the start-up transient,
    /// then start the pump. If the pump does not start, the recording is discarded: it holds no
    /// run and should not use up a run number.
    /// </summary>
    private async void OnRunPump(object sender, RoutedEventArgs e)
    {
        if (_session is null || !TryReadTarget(out double target) || !TryReadFlow(out double flow)) return;
        if (_saveDir is null)
        {
            ShowAlert(InfoBarSeverity.Warning, "No output folder", "Every run is recorded: choose an output folder first.");
            return;
        }

        _busy = true;
        UpdateControls();
        SafetyBar.IsOpen = false;
        PumpRecorder? recorder = null;
        try
        {
            recorder = PumpRecorder.Start(_session, _saveDir, CurrentSerialPadded, CurrentRn, _appVersion, CurrentGeometry);
            _recorder = recorder;
            _recordStartedUtc = DateTime.UtcNow;
            _sawRunning = false;
            _tailUntilUtc = null;
            StatsPanel.Visibility = Visibility.Visible;

            await _session.StartPumpAsync(target, flow);

            RememberTargetAndFlow(target, flow);
            ClearAlert();
            SetStatus($"Running · target {target:F0} psi · max flow {flow} mL/min · recording {recorder.Spool.Manifest.OutputFileName}",
                StatusKind.Recording);
        }
        catch (Exception ex) when (IsPumpError(ex) || ex is ArgumentException)
        {
            if (recorder is not null && ReferenceEquals(_recorder, recorder))
            {
                recorder.Discard();
                _recorder = null;
                StatsPanel.Visibility = Visibility.Collapsed;
            }
            ShowAlert(InfoBarSeverity.Error, "The pump was not started", ex.Message);
        }
        finally
        {
            _busy = false;
            UpdateControls();
        }
    }

    /// <summary>
    /// Stop the pump, then save the recording. Never gated on <see cref="_busy"/>: Stop must work
    /// while anything else is in flight. If Stop does not reach the pump, the recording carries on,
    /// because the pump may still be running.
    /// </summary>
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
                $"{ex.Message} Stop the pump with the RUN/STOP key on its front panel, or switch it off. The recording continues.");
            return;
        }

        await StopRecordingAsync();
    }

    /// <summary>The pump stopped by itself: keep recording the pressure decay for a few seconds, then save.</summary>
    private void BeginRecordingTail(string why)
    {
        if (_recorder is null || _tailUntilUtc is not null) return;
        _tailUntilUtc = DateTime.UtcNow.AddSeconds(PumpConfig.RecordTailSeconds);
        SetStatus($"{why} · saving the recording in {PumpConfig.RecordTailSeconds:F0} s", StatusKind.Busy);
    }

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_session is null || !TryReadTarget(out double target) || !TryReadFlow(out double flow)) return;

        _busy = true;
        UpdateControls();
        try
        {
            if (Math.Abs(target - _session.PressureSetPointPsi) > 0.5) await _session.SetTargetPressureAsync(target);
            if (Math.Abs(flow - _session.FlowLimit) > 1e-9) await _session.SetMaxFlowAsync(flow);
            RememberTargetAndFlow(target, flow);
            ClearAlert();
            SetStatus($"Applied: target {target:F0} psi · max flow {flow} mL/min",
                _session.LatestSample is { Running: true } ? StatusKind.Running : StatusKind.Connected);
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
            ShowAlert(InfoBarSeverity.Warning, "Check the max flow", $"Enter a max flow above 0 and at most {max} mL/min.");
            return false;
        }
        return true;
    }

    private bool TryReadTarget(out double target)
    {
        target = TargetBox.Value;
        if (double.IsNaN(target) || target <= 0 || target > PumpConfig.PressureCapPsi)
        {
            ShowAlert(InfoBarSeverity.Warning, "Check the target pressure",
                $"Enter a target above 0 and at most {PumpConfig.PressureCapPsi:F0} psi.");
            return false;
        }
        return true;
    }

    private void RememberTargetAndFlow(double target, double flow)
    {
        _settings.TargetPressurePsi = target;
        _settings.FlowMlMin = flow;
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
            TripReason.OverPressure => $"Pump stopped: pressure reached {PumpConfig.PressureCapPsi:F0} psi",
            _ => "Pump fault",
        };
        SafetyBar.Message = verdict.Message;
        SafetyBar.IsOpen = true;
        SetStatus(SafetyBar.Title, StatusKind.Error);
        BeginRecordingTail(SafetyBar.Title);
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

    private async Task StopRecordingAsync()
    {
        PumpRecorder? recorder = _recorder;
        if (recorder is null || _saveDir is null) return;
        _recorder = null;
        _tailUntilUtc = null;
        _sawRunning = false;
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

        FaultGeometry geometry = CurrentGeometry;
        PressureText.Text = s is null ? EmptyReadout : s.PressurePsi.ToString("F0", CultureInfo.InvariantCulture);
        FlowText.Text = s is null ? EmptyReadout : s.Flow.ToString("F" + decimals, CultureInfo.InvariantCulture);
        TargetText.Text = session is null ? EmptyReadout : session.PressureSetPointPsi.ToString("F0", CultureInfo.InvariantCulture);
        ShearText.Text = s is null ? EmptyReadout : geometry.ShearStressMPa(s.PressurePsi).ToString("F2", CultureInfo.InvariantCulture);
        ShearCaptionText.Text = $"SHEAR STRESS · {geometry.Type.ToLabel()} {geometry.EffectiveThicknessM * 100:0} cm";

        PumpPressureSettingsText.Text = session is null
            ? "Not connected."
            : $"On the pump now: PRESS {session.PressureSetPointPsi:F0} psi, the target · "
              + $"HI PR {(s?.PressureLimitPsi ?? PumpConfig.UpperLimitPsi):F0} psi, the stop limit (pinned) · "
              + $"max flow {session.FlowLimit.ToString("F" + decimals, CultureInfo.InvariantCulture)} mL/min.";

        SetStateTile(session, s);

        // Every run is recorded; a pump that stops by itself ends the recording after a short tail.
        if (_recorder is not null && s is not null)
        {
            if (s.Running) _sawRunning = true;
            else if (_sawRunning) BeginRecordingTail("The pump stopped");
        }
        if (_tailUntilUtc is DateTime until && DateTime.UtcNow >= until)
        {
            _tailUntilUtc = null;
            _ = StopRecordingAsync();
        }

        PressurePlot.ReferenceValue = session?.PressureSetPointPsi;
        PressurePlot.LimitValue = session is null ? null : s?.PressureLimitPsi ?? PumpConfig.UpperLimitPsi;
        FlowPlot.ReferenceValue = session?.FlowLimit;
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
        RunPumpButton.IsEnabled = connected && !_busy && !running && !fault && !recording && _saveDir is not null;
        StopPumpButton.IsEnabled = connected;   // always, whatever else is happening
        ClearFaultButton.IsEnabled = connected;

        // Everything that goes into the file name or the recording is fixed while one is open.
        ChooseFolderButton.IsEnabled = !recording;
        SerialBox.IsEnabled = !recording;
        RnBox.IsEnabled = !recording;
        SyncNamingButton.IsEnabled = !recording;
        Fault1DRadio.IsEnabled = !recording;
        Fault2DRadio.IsEnabled = !recording;
        FaultThicknessCombo.IsEnabled = !recording && CurrentFaultType == FaultType.OneD;
    }

    // ---------- fault geometry ----------

    private FaultType CurrentFaultType => Fault2DRadio.IsChecked == true ? FaultType.TwoD : FaultType.OneD;

    private FaultGeometry CurrentGeometry
    {
        get
        {
            (string Label, double Metres)[] options = UnitConversion.ThicknessOptions(CurrentFaultType);
            int index = Math.Clamp(FaultThicknessCombo.SelectedIndex, 0, options.Length - 1);
            return new FaultGeometry(CurrentFaultType, options[index].Metres);
        }
    }

    private void OnFaultTypeChanged(object sender, RoutedEventArgs e)
    {
        if (FaultThicknessCombo is null) return;

        (string Label, double Metres)[] options = UnitConversion.ThicknessOptions(CurrentFaultType);
        FaultThicknessCombo.Items.Clear();
        foreach ((string label, _) in options) FaultThicknessCombo.Items.Add(label);

        int saved = Array.FindIndex(options, o => Math.Abs(o.Metres - _settings.FaultThicknessM) < 1e-9);
        FaultThicknessCombo.SelectedIndex = saved >= 0 ? saved : 0;
        FaultThicknessCombo.IsEnabled = CurrentFaultType == FaultType.OneD && _recorder is null;
        SaveGeometry();
    }

    private void OnThicknessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FaultThicknessCombo.SelectedIndex >= 0) SaveGeometry();
    }

    private void SaveGeometry()
    {
        FaultGeometry g = CurrentGeometry;
        _settings.FaultType = g.Type.ToLabel();
        // 2D has a single, fixed thickness: keep the 1D choice for when 1D is selected again.
        if (g.Type == FaultType.OneD) _settings.FaultThicknessM = g.ThicknessM;
        _settings.Save(UserSettings.DefaultPath);
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
