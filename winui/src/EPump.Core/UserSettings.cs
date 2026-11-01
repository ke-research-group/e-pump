using System.Text.Json;
using System.Text.Json.Serialization;

namespace EPump.Core;

/// <summary>
/// What the operator set last time, restored on launch: the port, the flow and the pressure
/// limit. Stored as <c>%LOCALAPPDATA%\EPump\settings.json</c>; the pressure limit is
/// re-validated against the cap on load, so editing the file cannot raise it.
/// </summary>
public sealed class UserSettings
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EPump", "settings.json");

    public string Port { get; set; } = string.Empty;
    /// <summary>FI: the most the pump may deliver while holding the target.</summary>
    public double FlowMlMin { get; set; } = PumpConfig.DefaultMaxFlowMlMin;

    /// <summary>PS: the target pressure. Never above the cap, whatever the file says.</summary>
    public double TargetPressurePsi { get; set; } = PumpConfig.DefaultTargetPressurePsi;
    public bool LockKeypad { get; set; } = true;

    /// <summary>"1D" or "2D".</summary>
    public string FaultType { get; set; } = "1D";

    public double FaultThicknessM { get; set; } = 0.10;

    public static UserSettings Load(string path)
    {
        UserSettings s;
        try
        {
            s = File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), UserSettingsJsonContext.Default.UserSettings) ?? new UserSettings()
                : new UserSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            s = new UserSettings();   // a corrupt file must not stop the app from starting
        }

        if (!(s.TargetPressurePsi > 0 && s.TargetPressurePsi <= PumpConfig.PressureCapPsi))
            s.TargetPressurePsi = PumpConfig.DefaultTargetPressurePsi;
        if (!(s.FlowMlMin > 0)) s.FlowMlMin = PumpConfig.DefaultMaxFlowMlMin;
        if (s.FaultType is not ("1D" or "2D")) s.FaultType = "1D";
        return s;
    }

    /// <summary>Write via a temporary file and an atomic replace. Failures are swallowed: losing the memory is a nuisance, not a fault.</summary>
    public void Save(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, UserSettingsJsonContext.Default.UserSettings));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UserSettings))]
internal partial class UserSettingsJsonContext : JsonSerializerContext
{
}
