using System.Diagnostics;
using System.Globalization;
using ClapBack.Core;

namespace ClapBack.Stt.Audio;

/// <summary>归一化结果：<b>永远</b>是 16 kHz / 单声道 / 16 bit PCM 的 WAV 字节。</summary>
public sealed record AudioNormalizeResult(
    byte[] WaveBytes,
    int SampleRate,
    int Channels,
    AudioContainer SourceContainer,
    bool UsedExternalConverter);

/// <summary>把任意受支持的音频输入归一化成本地识别运行时需要的 16 kHz 单声道 16 bit PCM WAV。</summary>
public interface IAudioNormalizer
{
    Task<AudioNormalizeResult> NormalizeToWavePcm16Mono16KAsync(
        Stream audio,
        string? fileName,
        string? contentType,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 音频归一化。两条路径：
/// <list type="number">
/// <item><b>WAV 走纯托管解码</b>——PCM(1)、IEEE float(3)、WAVE_FORMAT_EXTENSIBLE(0xFFFE)，
/// 8/16/24/32 bit，多声道下混单声道后线性插值重采样到 16000 Hz。不依赖任何外部程序。</item>
/// <item><b>非 WAV 走 ffmpeg</b>——只用 <c>ArgumentList</c> 逐个传参（绝不字符串拼接，杜绝命令注入），
/// 60 秒超时后 <c>Kill(entireProcessTree: true)</c>，临时文件带 GUID 放在应用的便携数据目录。</item>
/// </list>
/// 没有 ffmpeg 时非 WAV 输入抛 <see cref="NotSupportedException"/>，而不是悄悄发一段错格式的音频。
/// </summary>
public sealed class AudioNormalizer : IAudioNormalizer
{
    public const int TargetSampleRate = 16000;
    public const int TargetChannels = 1;

    private static readonly TimeSpan ConverterTimeout = TimeSpan.FromSeconds(60);

    private readonly string? _ffmpegPath;

    public AudioNormalizer(string? ffmpegPath = null) => _ffmpegPath = ffmpegPath;

    public async Task<AudioNormalizeResult> NormalizeToWavePcm16Mono16KAsync(
        Stream audio,
        string? fileName,
        string? contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);

        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var input = buffer.ToArray();
        if (input.Length == 0)
        {
            throw new InvalidDataException("音频内容为空。");
        }

        var container = AudioFormatDetector.Detect(input.AsSpan(0, Math.Min(input.Length, 32)).ToArray());
        if (container == AudioContainer.Unknown)
        {
            container = ContainerFromMetadata(fileName, contentType);
        }

        if (container == AudioContainer.Wav)
        {
            var decoded = DecodeWav(input);
            var resampled = ResampleToMono16K(decoded.Samples, decoded.SampleRate, decoded.Channels);
            return new AudioNormalizeResult(
                EncodePcm16Wave(resampled, TargetSampleRate),
                TargetSampleRate,
                TargetChannels,
                AudioContainer.Wav,
                UsedExternalConverter: false);
        }

        return await ConvertWithFfmpegAsync(input, container, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- WAV 纯托管解码

    private readonly record struct DecodedWav(float[] Samples, int SampleRate, int Channels);

    private static DecodedWav DecodeWav(byte[] input)
    {
        if (input.Length < 12 ||
            input[0] != (byte)'R' || input[1] != (byte)'I' || input[2] != (byte)'F' || input[3] != (byte)'F' ||
            input[8] != (byte)'W' || input[9] != (byte)'A' || input[10] != (byte)'V' || input[11] != (byte)'E')
        {
            throw new InvalidDataException("不是合法的 RIFF/WAVE 文件。");
        }

        ushort formatTag = 0;
        var channels = 0;
        var sampleRate = 0;
        var bitsPerSample = 0;
        var haveFormat = false;
        var dataOffset = -1;
        var dataLength = 0;

        // 逐 chunk 扫描：容忍未知 chunk、data 在 fmt 之前、声明长度超过实际剩余字节。
        var position = 12;
        while (position + 8 <= input.Length)
        {
            var chunkId = BitConverter.ToUInt32(input, position);
            var declaredSize = BitConverter.ToInt32(input, position + 4);
            var payloadStart = position + 8;
            if (declaredSize < 0)
            {
                throw new InvalidDataException("WAV chunk 长度非法。");
            }

            // 声明长度超过实际剩余内容时以实际为准，避免被截断的录音整个被拒。
            var payloadLength = Math.Min(declaredSize, input.Length - payloadStart);

            if (chunkId == 0x20746D66 /* "fmt " */ && payloadLength >= 16)
            {
                formatTag = BitConverter.ToUInt16(input, payloadStart);
                channels = BitConverter.ToUInt16(input, payloadStart + 2);
                sampleRate = BitConverter.ToInt32(input, payloadStart + 4);
                bitsPerSample = BitConverter.ToUInt16(input, payloadStart + 14);

                // WAVE_FORMAT_EXTENSIBLE：真实格式在 SubFormat GUID 的前 2 字节。
                if (formatTag == 0xFFFE && payloadLength >= 26)
                {
                    formatTag = BitConverter.ToUInt16(input, payloadStart + 24);
                }

                haveFormat = true;
            }
            else if (chunkId == 0x61746164 /* "data" */ && dataOffset < 0)
            {
                dataOffset = payloadStart;
                dataLength = payloadLength;
            }

            // chunk 按偶数字节对齐。
            position = payloadStart + declaredSize + (declaredSize & 1);
        }

        if (!haveFormat)
        {
            throw new InvalidDataException("WAV 文件缺少 fmt chunk。");
        }

        if (dataOffset < 0 || dataLength <= 0)
        {
            throw new InvalidDataException("WAV 文件缺少 data chunk。");
        }

        if (channels < 1)
        {
            throw new InvalidDataException("WAV 文件的声道数非法。");
        }

        if (sampleRate < 1000 || sampleRate > 384000)
        {
            throw new InvalidDataException("WAV 文件的采样率非法。");
        }

        var bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample is not (1 or 2 or 3 or 4))
        {
            throw new NotSupportedException($"不支持的 WAV 位深：{bitsPerSample} bit。");
        }

        var frameCount = dataLength / (bytesPerSample * channels);
        if (frameCount == 0)
        {
            throw new InvalidDataException("WAV 文件不含任何采样帧。");
        }

        var mono = new float[frameCount];
        for (var frame = 0; frame < frameCount; frame++)
        {
            double sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                var index = dataOffset + ((frame * channels) + channel) * bytesPerSample;
                sum += ReadSample(input, index, formatTag, bitsPerSample);
            }

            mono[frame] = (float)(sum / channels);
        }

        return new DecodedWav(mono, sampleRate, channels);
    }

    private static double ReadSample(byte[] input, int index, ushort formatTag, int bitsPerSample)
    {
        if (formatTag == 3 /* IEEE float */)
        {
            return bitsPerSample switch
            {
                32 => BitConverter.ToSingle(input, index),
                64 => BitConverter.ToDouble(input, index),
                _ => throw new NotSupportedException($"不支持的 WAV 浮点位深：{bitsPerSample} bit。"),
            };
        }

        if (formatTag != 1 /* PCM */)
        {
            throw new NotSupportedException($"不支持的 WAV 编码格式：0x{formatTag:X4}。");
        }

        return bitsPerSample switch
        {
            // 8 bit PCM 是无符号的，128 是零点。
            8 => (input[index] - 128) / 128.0,
            16 => BitConverter.ToInt16(input, index) / 32768.0,
            24 => ReadInt24(input, index) / 8388608.0,
            32 => BitConverter.ToInt32(input, index) / 2147483648.0,
            _ => throw new NotSupportedException($"不支持的 WAV 位深：{bitsPerSample} bit。"),
        };
    }

    private static int ReadInt24(byte[] input, int index)
    {
        var value = input[index] | (input[index + 1] << 8) | (input[index + 2] << 16);
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    /// <summary>多声道下混单声道，再线性插值重采样到 16000 Hz。</summary>
    private static float[] ResampleToMono16K(float[] samples, int sourceRate, int channels)
    {
        if (sourceRate == TargetSampleRate && channels == TargetChannels)
        {
            return samples;
        }

        var outputLength = (int)Math.Ceiling(samples.Length * (double)TargetSampleRate / sourceRate);
        if (outputLength <= 0)
        {
            return Array.Empty<float>();
        }

        var output = new float[outputLength];
        if (samples.Length == 1)
        {
            Array.Fill(output, samples[0]);
            return output;
        }

        // 线性插值对语音识别足够，且不需要额外依赖。
        var step = (double)(samples.Length - 1) / Math.Max(outputLength - 1, 1);
        for (var i = 0; i < outputLength; i++)
        {
            var position = i * step;
            var left = (int)position;
            var right = Math.Min(left + 1, samples.Length - 1);
            var weight = (float)(position - left);
            output[i] = samples[left] + ((samples[right] - samples[left]) * weight);
        }

        return output;
    }

    private static byte[] EncodePcm16Wave(float[] samples, int sampleRate)
    {
        var dataSize = samples.Length * 2;
        var output = new byte[44 + dataSize];

        WriteAscii(output, 0, "RIFF");
        BitConverter.TryWriteBytes(output.AsSpan(4), 36 + dataSize);
        WriteAscii(output, 8, "WAVE");
        WriteAscii(output, 12, "fmt ");
        BitConverter.TryWriteBytes(output.AsSpan(16), 16);
        BitConverter.TryWriteBytes(output.AsSpan(20), (ushort)1);       // PCM
        BitConverter.TryWriteBytes(output.AsSpan(22), (ushort)1);       // 单声道
        BitConverter.TryWriteBytes(output.AsSpan(24), sampleRate);
        BitConverter.TryWriteBytes(output.AsSpan(28), sampleRate * 2);  // byte rate
        BitConverter.TryWriteBytes(output.AsSpan(32), (ushort)2);       // block align
        BitConverter.TryWriteBytes(output.AsSpan(34), (ushort)16);      // bits per sample
        WriteAscii(output, 36, "data");
        BitConverter.TryWriteBytes(output.AsSpan(40), dataSize);

        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            var value = (short)Math.Round(clamped * short.MaxValue, MidpointRounding.AwayFromZero);
            BitConverter.TryWriteBytes(output.AsSpan(44 + (i * 2)), value);
        }

        return output;
    }

    private static void WriteAscii(byte[] target, int offset, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            target[offset + i] = (byte)text[i];
        }
    }

    // ---------------------------------------------------------------- 非 WAV：ffmpeg

    private async Task<AudioNormalizeResult> ConvertWithFfmpegAsync(
        byte[] input,
        AudioContainer container,
        CancellationToken cancellationToken)
    {
        var ffmpeg = ResolveFfmpegPath();
        var directory = CreateTemporaryDirectory();

        var inputPath = Path.Combine(directory, $"clapback-{Guid.NewGuid():N}.in");
        var outputPath = Path.Combine(directory, $"clapback-{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(inputPath, input, cancellationToken).ConfigureAwait(false);

            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            // 只用 ArgumentList 逐项传参，路径里出现空格或分号都不会变成额外参数。
            process.StartInfo.ArgumentList.Add("-nostdin");
            process.StartInfo.ArgumentList.Add("-v");
            process.StartInfo.ArgumentList.Add("error");
            process.StartInfo.ArgumentList.Add("-y");
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(inputPath);
            process.StartInfo.ArgumentList.Add("-vn");
            process.StartInfo.ArgumentList.Add("-ac");
            process.StartInfo.ArgumentList.Add(TargetChannels.ToString(CultureInfo.InvariantCulture));
            process.StartInfo.ArgumentList.Add("-ar");
            process.StartInfo.ArgumentList.Add(TargetSampleRate.ToString(CultureInfo.InvariantCulture));
            process.StartInfo.ArgumentList.Add("-acodec");
            process.StartInfo.ArgumentList.Add("pcm_s16le");
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add("wav");
            process.StartInfo.ArgumentList.Add(outputPath);

            try
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("无法启动音频转换器（ffmpeg）。");
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new NotSupportedException(
                    $"无法启动音频转换器（ffmpeg）：{ex.Message}。请安装 ffmpeg，或在设置 FfmpegPath 中指定它的完整路径。",
                    ex);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConverterTimeout);

            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException($"音频转换超过 {ConverterTimeout.TotalSeconds:F0} 秒，已终止。");
            }

            if (process.ExitCode != 0)
            {
                TryKill(process);
                var detail = Sanitize(await stderrTask.ConfigureAwait(false));
                throw new InvalidOperationException(
                    $"音频转换失败（{container}），ffmpeg 退出码 {process.ExitCode}：{detail}");
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                throw new InvalidDataException("音频转换没有产生任何输出。");
            }

            var wave = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            return new AudioNormalizeResult(
                wave,
                TargetSampleRate,
                TargetChannels,
                container,
                UsedExternalConverter: true);
        }
        finally
        {
            TryDelete(inputPath);
            TryDelete(outputPath);
        }
    }

    private string ResolveFfmpegPath()
    {
        if (!string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            return _ffmpegPath;
        }

        foreach (var candidate in new[] { "ffmpeg", "ffmpeg.exe" })
        {
            try
            {
                using var probe = new Process();
                probe.StartInfo = new ProcessStartInfo
                {
                    FileName = candidate,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                probe.StartInfo.ArgumentList.Add("-version");
                if (probe.Start())
                {
                    probe.Kill(entireProcessTree: true);
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // 找不到就继续试下一个候选名。
            }
        }

        throw new NotSupportedException(
            "本机没有 ffmpeg，无法解码非 WAV 音频。请安装 ffmpeg，或在设置 FfmpegPath 中指定它的完整路径。");
    }

    internal static string CreateTemporaryDirectory()
    {
        var directory = AppPaths.TempAudioDirectory;
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已经退出。
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 权限不足杀不掉，交由 finally 清理临时文件。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 尽力而为；残留文件由 CleanupStaleFiles 在下次使用时回收。
        }
        catch (UnauthorizedAccessException)
        {
            // 尽力而为。
        }
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        compact = compact
            .Replace(AppContext.BaseDirectory, "<app>", StringComparison.OrdinalIgnoreCase)
            .Replace(Path.GetTempPath(), "<temp>", StringComparison.OrdinalIgnoreCase)
            .Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<home>", StringComparison.OrdinalIgnoreCase);

        return compact.Length <= 300 ? compact : compact[..300];
    }

    private static AudioContainer ContainerFromMetadata(string? fileName, string? contentType)
    {
        var byExtension = AudioFormatDetector.Detect(Array.Empty<byte>());
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        byExtension = extension switch
        {
            ".wav" or ".wave" => AudioContainer.Wav,
            ".mp3" => AudioContainer.Mp3,
            ".flac" => AudioContainer.Flac,
            ".ogg" or ".oga" => AudioContainer.Ogg,
            ".m4a" or ".mp4" => AudioContainer.Mp4,
            ".webm" => AudioContainer.Webm,
            ".aac" => AudioContainer.Aac,
            ".aif" or ".aiff" or ".aifc" => AudioContainer.Aiff,
            _ => byExtension,
        };

        if (byExtension != AudioContainer.Unknown)
        {
            return byExtension;
        }

        return (contentType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "audio/wav" or "audio/wave" or "audio/x-wav" => AudioContainer.Wav,
            "audio/mpeg" or "audio/mp3" => AudioContainer.Mp3,
            "audio/flac" or "audio/x-flac" => AudioContainer.Flac,
            "audio/ogg" or "application/ogg" => AudioContainer.Ogg,
            "audio/mp4" or "audio/m4a" => AudioContainer.Mp4,
            "audio/webm" => AudioContainer.Webm,
            "audio/aac" => AudioContainer.Aac,
            "audio/aiff" or "audio/x-aiff" => AudioContainer.Aiff,
            _ => AudioContainer.Unknown,
        };
    }
}
