using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerMonitor.Core;

public enum TrayDisplay { Watts, Percent, Icon }

/// <summary>
/// User settings shared by the app (writes) and the collector (reads), stored next to
/// the database as settings.json. The collector re-reads it when the file changes.
/// </summary>
public sealed record Settings
{
    public bool TrayIcon { get; init; } = true;
    [JsonConverter(typeof(JsonStringEnumConverter<TrayDisplay>))]
    public TrayDisplay TrayDisplay { get; init; } = TrayDisplay.Watts;
    /// <summary>Notifications on battery: dGPU awake for minutes, sustained high draw.</summary>
    public bool Alerts { get; init; } = true;

    public static string FilePath => Path.Combine(Path.GetDirectoryName(Store.DefaultPath)!, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.Settings) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, SettingsJson.Default.Settings));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

/// <summary>Source-generated (the collector is NativeAOT: no reflection-based JSON).</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
partial class SettingsJson : JsonSerializerContext;
