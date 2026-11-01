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
    public double FlowMlMin { get; set; } = 1.0;
    public double PressureLimitPsi { get; set; } = PumpConfig.DefaultPressureLimitPsi;
    public bool LockKeypad { get; set; } = true;

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

        if (!(s.PressureLimitPsi > 0 && s.PressureLimitPsi <= PumpConfig.PressureCapPsi))
            s.PressureLimitPsi = PumpConfig.DefaultPressureLimitPsi;
        if (!(s.FlowMlMin > 0)) s.FlowMlMin = 1.0;
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
