using System.IO;
using System.Text.Json;

namespace PhoneCam.Settings;

public sealed class AppSettings
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PhoneCam");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public int Fps { get; set; } = 30;
    public int BitrateMbps { get; set; } = 6;
    public bool UseFrontCamera { get; set; }
    public double Zoom { get; set; } = 1.0;
    public bool MirrorHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    public int ManualRotationOffsetQuarters { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool DebugLogEnabled { get; set; }
    public bool MicEnabled { get; set; }
    public bool MicMuted { get; set; }
    public string PreferredAudioDeviceId { get; set; } = "";
    public double MicGain { get; set; } = 1.0;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
        }
    }
}

