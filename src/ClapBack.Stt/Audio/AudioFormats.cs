namespace ClapBack.Stt.Audio;

/// <summary>已识别出的音频容器格式。用于选择解码路径和上报正确的 MIME 类型。</summary>
public enum AudioContainer
{
    Unknown = 0,
    Wav = 1,
    Mp3 = 2,
    Flac = 3,
    Ogg = 4,
    Mp4 = 5,
    Webm = 6,
    Aac = 7,
    Aiff = 8,
}

/// <summary>
/// 按文件头的魔数识别音频容器。
/// 只看扩展名是不够的：浏览器/游戏录屏拿到的文件名经常是 <c>blob</c> 或 <c>recording</c>，
/// 而按错误的格式把音频发给云端会直接导致识别失败或乱码。
/// </summary>
public static class AudioFormatDetector
{
    /// <summary>只读取文件头，不做任何 I/O。返回 <see cref="AudioContainer.Unknown"/> 表示认不出。</summary>
    public static AudioContainer Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 12 &&
            header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F' &&
            header[8] == (byte)'W' && header[9] == (byte)'A' && header[10] == (byte)'V' && header[11] == (byte)'E')
        {
            return AudioContainer.Wav;
        }

        if (header.Length >= 12 &&
            header[0] == (byte)'F' && header[1] == (byte)'O' && header[2] == (byte)'R' && header[3] == (byte)'M')
        {
            // AIFF / AIFC
            return AudioContainer.Aiff;
        }

        if (header.Length >= 4 &&
            header[0] == (byte)'f' && header[1] == (byte)'L' && header[2] == (byte)'a' && header[3] == (byte)'C')
        {
            return AudioContainer.Flac;
        }

        if (header.Length >= 4 &&
            header[0] == (byte)'O' && header[1] == (byte)'g' && header[2] == (byte)'g' && header[3] == (byte)'S')
        {
            return AudioContainer.Ogg;
        }

        if (header.Length >= 4 &&
            header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3)
        {
            return AudioContainer.Webm;
        }

        if (header.Length >= 12 &&
            header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p')
        {
            return AudioContainer.Mp4;
        }

        if (header.Length >= 3 && header[0] == (byte)'I' && header[1] == (byte)'D' && header[2] == (byte)'3')
        {
            return AudioContainer.Mp3;
        }

        if (header.Length >= 2 && header[0] == 0xFF)
        {
            // MPEG 帧同步：11 个 1 位。区分 ADTS AAC 与 MPEG 音频。
            if ((header[1] & 0xF6) == 0xF0)
            {
                return AudioContainer.Aac;
            }

            if ((header[1] & 0xE0) == 0xE0)
            {
                return AudioContainer.Mp3;
            }
        }

        return AudioContainer.Unknown;
    }

    /// <summary>
    /// 读取流的开头若干字节识别容器。<b>非破坏性</b>：读完把 <c>Position</c> 复位到进入时的值，
    /// 调用方可以继续把同一条流当原始数据用。不可 Seek 的流返回 <see cref="AudioContainer.Unknown"/>。
    /// </summary>
    public static AudioContainer Detect(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead || !content.CanSeek)
        {
            return AudioContainer.Unknown;
        }

        var origin = content.Position;
        try
        {
            Span<byte> header = stackalloc byte[32];
            var read = 0;
            while (read < header.Length)
            {
                var n = content.Read(header[read..]);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            return Detect(header[..read]);
        }
        finally
        {
            content.Position = origin;
        }
    }
}

/// <summary>容器到 MIME 类型的映射，用于 multipart 上传时如实告知云端文件格式。</summary>
public static class AudioMimeType
{
    private const string Default = "application/octet-stream";

    /// <summary>
    /// 以文件头识别结果为准，扩展名只在文件头认不出时兜底。
    /// 之前这里硬编码 <c>audio/wav</c> 加 <c>clapback.wav</c>，任何非 WAV 输入都会被当成 WAV 上传。
    /// </summary>
    public static string Resolve(AudioContainer container, string? fileName)
    {
        if (container != AudioContainer.Unknown)
        {
            return FromContainer(container);
        }

        return FromFileName(fileName);
    }

    private static string FromContainer(AudioContainer container) => container switch
    {
        AudioContainer.Wav => "audio/wav",
        AudioContainer.Mp3 => "audio/mpeg",
        AudioContainer.Flac => "audio/flac",
        AudioContainer.Ogg => "audio/ogg",
        AudioContainer.Mp4 => "audio/mp4",
        AudioContainer.Webm => "audio/webm",
        AudioContainer.Aac => "audio/aac",
        AudioContainer.Aiff => "audio/aiff",
        _ => Default,
    };

    private static string FromFileName(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        return extension switch
        {
            ".wav" or ".wave" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".flac" => "audio/flac",
            ".ogg" or ".oga" => "audio/ogg",
            ".m4a" or ".mp4" or ".aac" => "audio/mp4",
            ".webm" => "audio/webm",
            ".aif" or ".aiff" or ".aifc" => "audio/aiff",
            _ => Default,
        };
    }

    /// <summary>给原始音频挑一个与真实格式一致的文件名，避免云端按错误格式解析。</summary>
    public static string ResolveFileName(AudioContainer container, string? fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "clapback";
        }

        var extension = container switch
        {
            AudioContainer.Wav => ".wav",
            AudioContainer.Mp3 => ".mp3",
            AudioContainer.Flac => ".flac",
            AudioContainer.Ogg => ".ogg",
            AudioContainer.Mp4 => ".m4a",
            AudioContainer.Webm => ".webm",
            AudioContainer.Aac => ".aac",
            AudioContainer.Aiff => ".aiff",
            _ => Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant(),
        };

        return stem + extension;
    }
}
