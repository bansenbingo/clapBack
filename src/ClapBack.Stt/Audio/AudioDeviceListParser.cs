using System.Text.Json;
using ClapBack.Core.Abstractions;

namespace ClapBack.Stt.Audio;

/// <summary>
/// 解析麦克风设备枚举脚本输出的 JSON：<c>{"devices":[{"index":0,"name":"..."}]}</c>。
///
/// 独立成一个纯函数是为了能被测试覆盖——设备枚举本身依赖本地音频栈，
/// 但「拿到这段 JSON 该怎么解释」应该有确定的答案。
/// 任何不认识的形状都返回空列表，而不是抛异常：设备枚举失败不该让整个 GUI 起不来。
/// </summary>
public static class AudioDeviceListParser
{
    public static IReadOnlyList<AudioInputDevice> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<AudioInputDevice>();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Array.Empty<AudioInputDevice>();
        }

        using (document)
        {
            // 根节点必须是对象才能取属性——根节点是数组/字符串时 TryGetProperty 会抛 InvalidOperationException，
            // 那会让设备枚举把整个 GUI 拖垮。这里直接判形状。
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("devices", out var devices) ||
                devices.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<AudioInputDevice>();
            }

            var result = new List<AudioInputDevice>();
            foreach (var entry in devices.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!entry.TryGetProperty("index", out var indexElement) ||
                    indexElement.ValueKind != JsonValueKind.Number ||
                    !entry.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var index = indexElement.GetInt32();
                if (index < 0)
                {
                    continue;
                }

                var name = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = $"输入设备 {index}";
                }

                var trimmed = name.Trim();
                result.Add(new AudioInputDevice(trimmed, trimmed, index));
            }

            return result;
        }
    }
}
