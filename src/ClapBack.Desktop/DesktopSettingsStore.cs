using System.Text.Json;
using ClapBack.Core.Models;

namespace ClapBack.Desktop;

internal static class DesktopSettingsStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static string PathToSettings =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClapBack",
            "desktop-settings.json");

    public static ClapBackSettings Load()
    {
        try
        {
            if (!File.Exists(PathToSettings)) return new ClapBackSettings();
            return JsonSerializer.Deserialize<ClapBackSettings>(File.ReadAllText(PathToSettings), Options)
                   ?? new ClapBackSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new ClapBackSettings();
        }
    }

    public static void Save(ClapBackSettings settings)
    {
        var directory = Path.GetDirectoryName(PathToSettings)
                        ?? throw new InvalidOperationException("无法确定设置目录");
        Directory.CreateDirectory(directory);
        File.WriteAllText(PathToSettings, JsonSerializer.Serialize(settings, Options));
    }
}
