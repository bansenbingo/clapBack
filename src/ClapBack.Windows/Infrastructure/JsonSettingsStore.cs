using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;

namespace ClapBack.Windows.Infrastructure;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static JsonSettingsStore()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClapBack",
        "settings.json");

    public async Task<ClapBackSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return new ClapBackSettings();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<ClapBackSettings>(stream, JsonOptions, cancellationToken)
                   ?? new ClapBackSettings();
        }
        catch (JsonException)
        {
            return new ClapBackSettings();
        }
        catch (IOException)
        {
            return new ClapBackSettings();
        }
    }

    public async Task SaveAsync(ClapBackSettings settings, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidOperationException("无法确定配置目录");
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken);
        }

        File.Move(temporaryPath, _path, true);
    }
}
