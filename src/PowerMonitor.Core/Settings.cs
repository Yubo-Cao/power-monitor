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

    public static string FilePath => Path.Combine(Path.GetDirectoryName(Store.DefaultPath)!, "settings.json");

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
