using System.Text.Json;
using System.Text.Json.Serialization;
using ClapBack.Core.Models;

namespace ClapBack.Desktop;

/// <summary>
/// 枚举只按<b>名字</b>写盘、读盘，且读到不认识的值就抛错让整份设置作废。
/// 旧版本按数字持久化枚举，一旦枚举成员顺序变化，数字就会指到另一个成员上——
/// 而 <see cref="TranscriptionMode"/> 的错误值可能直接导致「未明确同意就上传音频」。
/// 因此这里宁可让整份设置回退到默认值（默认是 LocalOnly，绝不上传），也不猜。
/// </summary>
internal sealed class NameOnlyEnumConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"{typeof(T).Name} 必须以名称字符串持久化（例如 \"LocalOnly\"），不接受数字。");
        }

        var text = reader.GetString();
        if (!Enum.TryParse<T>(text, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new JsonException($"无法识别的 {typeof(T).Name} 值“{text}”。");
        }

        return parsed;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

internal static class DesktopSettingsStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters =
        {
            new NameOnlyEnumConverter<TranscriptionMode>(),
            new NameOnlyEnumConverter<LocalModelVariant>(),
            new NameOnlyEnumConverter<ModelDownloadSource>()
        }
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
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 设置损坏或不可读时回到默认值：默认 TranscriptionMode 是 LocalOnly，
            // 保证「配置坏了」不会变成「自动上传音频」。
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
