using ClapBack.Stt.Audio;
using Xunit;

namespace ClapBack.Tests.Audio;

/// <summary>音频格式识别与归一化的回归测试。目标：任何输入都只能变成 16 kHz 单声道 16 bit PCM WAV。</summary>
public class AudioNormalizerTests
{
    // ---------------------------------------------------------------- 容器识别

    [Fact]
    public void Detect_RecognizesWavRiffWave()
    {
        Assert.Equal(AudioContainer.Wav, AudioFormatDetector.Detect(BuildWav(1, 1, 16000, 16, new byte[32])));
    }

    [Fact]
    public void Detect_RecognizesMp3Id3Tag()
    {
        var header = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Equal(AudioContainer.Mp3, AudioFormatDetector.Detect(header));
    }

    [Fact]
    public void Detect_RecognizesFlac()
    {
        var header = new byte[] { (byte)'f', (byte)'L', (byte)'a', (byte)'C', 0, 0, 0, 34 };
        Assert.Equal(AudioContainer.Flac, AudioFormatDetector.Detect(header));
    }

    [Fact]
    public void Detect_RecognizesOgg()
    {
        var header = new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S', 0, 2, 0, 0 };
        Assert.Equal(AudioContainer.Ogg, AudioFormatDetector.Detect(header));
    }

    [Fact]
    public void Detect_RecognizesMp4ByFtyp()
    {
        // 0..3 是 box 长度，4..7 是 "ftyp"。
        var header = new byte[] { 0, 0, 0, 32, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'M', (byte)'4', (byte)'A', (byte)' ' };
        Assert.Equal(AudioContainer.Mp4, AudioFormatDetector.Detect(header));
    }

    [Fact]
    public void Detect_RecognizesWebmByEbmlMagic()
    {
        var header = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x00, 0x00, 0x00 };
        Assert.Equal(AudioContainer.Webm, AudioFormatDetector.Detect(header));
    }

    [Fact]
    public void Detect_RecognizesAdtsAac()
    {
        var header = new byte[] { 0xFF, 0xF1, 0x50, 0x80, 0x01, 0x00, 0x00, 0x00 };
        Assert.Equal(AudioContainer.Aac, AudioFormatDetector.Detect(header));
    }

    [Fact]
    public void Detect_ReturnsUnknownForGarbage()
    {
        Assert.Equal(AudioContainer.Unknown, AudioFormatDetector.Detect(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.Equal(AudioContainer.Unknown, AudioFormatDetector.Detect(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Detect_StreamRewindsPosition()
    {
        var payload = BuildWav(1, 1, 16000, 16, new byte[32]);
        using var stream = new MemoryStream(payload);

        Assert.Equal(AudioContainer.Wav, AudioFormatDetector.Detect(stream));
        Assert.Equal(0, stream.Position);

        // 复位后仍能读到完整内容，说明识别没有把数据吃掉。
        var reread = new byte[payload.Length];
        Assert.Equal(payload.Length, stream.Read(reread, 0, reread.Length));
    }

    // ---------------------------------------------------------------- MIME 类型

    [Fact]
    public void MimeType_UsesRealContainerNotFileExtension()
    {
        // 文件名骗人时必须以文件头为准。
        Assert.Equal("audio/mpeg", AudioMimeType.Resolve(AudioContainer.Mp3, "clapback.wav"));
        Assert.Equal("audio/flac", AudioMimeType.Resolve(AudioContainer.Flac, "recording.wav"));
    }

    [Fact]
    public void MimeType_FallsBackToExtensionWhenContainerUnknown()
    {
        Assert.Equal("audio/wav", AudioMimeType.Resolve(AudioContainer.Unknown, "a.wav"));
        Assert.Equal("audio/mpeg", AudioMimeType.Resolve(AudioContainer.Unknown, "a.MP3"));
        Assert.Equal("application/octet-stream", AudioMimeType.Resolve(AudioContainer.Unknown, "a.bin"));
    }

    [Fact]
    public void MimeType_UploadNameCarriesMatchingExtension()
    {
        Assert.Equal("clapback.mp3", AudioMimeType.ResolveFileName(AudioContainer.Mp3, "clapback.wav"));
        Assert.Equal("voice.m4a", AudioMimeType.ResolveFileName(AudioContainer.Mp4, "voice.bin"));
    }

    // ---------------------------------------------------------------- WAV 归一化

    [Fact]
    public async Task Normalize_Wav16kMono_AlreadyTargetFormat()
    {
        var pcm = new byte[200];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            BitConverter.TryWriteBytes(pcm.AsSpan(i), (short)(i * 32));
        }

        var normalizer = new AudioNormalizer();
        var result = await normalizer.NormalizeToWavePcm16Mono16KAsync(
            new MemoryStream(BuildWav(1, 1, 16000, 16, pcm)), "a.wav", "audio/wav");

        Assert.Equal(16000, result.SampleRate);
        Assert.Equal(1, result.Channels);
        Assert.False(result.UsedExternalConverter);
        Assert.Equal(AudioContainer.Wav, result.SourceContainer);
        Assert.Equal(44 + 200, result.WaveBytes.Length);
    }

    [Fact]
    public async Task Normalize_Wav48kStereo_DownmixesAndResamplesTo16kMono()
    {
        // 4800 帧 @48 kHz → 1600 帧 @16 kHz。
        var frames = 4800;
        var pcm = new byte[frames * 2 /* stereo */ * 2 /* 16 bit */];
        for (var i = 0; i < pcm.Length; i += 2)
        {
            BitConverter.TryWriteBytes(pcm.AsSpan(i), (short)8000);
        }

        var normalizer = new AudioNormalizer();
        var result = await normalizer.NormalizeToWavePcm16Mono16KAsync(
            new MemoryStream(BuildWav(1, 2, 48000, 16, pcm)), "a.wav", "audio/wav");

        Assert.Equal(16000, result.SampleRate);
        Assert.Equal(1, result.Channels);
        Assert.Equal(44 + (frames * 16000 / 48000 * 2), result.WaveBytes.Length);
    }

    [Fact]
    public async Task Normalize_Wav8BitUnsigned_IsConvertedTo16Bit()
    {
        // 8 bit PCM 是无符号的，128 是零点；这里给一段直流 255（正满幅）。
        var pcm = Enumerable.Repeat((byte)255, 100).ToArray();

        var normalizer = new AudioNormalizer();
        var result = await normalizer.NormalizeToWavePcm16Mono16KAsync(
            new MemoryStream(BuildWav(1, 1, 16000, 8, pcm)), "a.wav", "audio/wav");

        Assert.Equal(44 + 200, result.WaveBytes.Length);
        var first = BitConverter.ToInt16(result.WaveBytes, 44);
        Assert.True(first > 30000, $"8 bit 满幅应映射到接近 16 bit 满幅，实际 {first}");
    }

    [Fact]
    public async Task Normalize_Wav24Bit_IsSupported()
    {
        var pcm = new byte[300];
        for (var i = 0; i < pcm.Length; i += 3)
        {
            pcm[i + 2] = 0x20; // 小端，高位字节在后
        }

        var normalizer = new AudioNormalizer();
        var result = await normalizer.NormalizeToWavePcm16Mono16KAsync(
            new MemoryStream(BuildWav(1, 1, 16000, 24, pcm)), "a.wav", "audio/wav");

        Assert.Equal(44 + (100 * 2), result.WaveBytes.Length);
    }

    [Fact]
    public async Task Normalize_WavFloat32_IsSupported()
    {
        var samples = new float[100];
        var pcm = new byte[samples.Length * 4];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = 0.5f;
            BitConverter.TryWriteBytes(pcm.AsSpan(i * 4), 0.5f);
        }

        var normalizer = new AudioNormalizer();
        var result = await normalizer.NormalizeToWavePcm16Mono16KAsync(
            new MemoryStream(BuildWav(3, 1, 16000, 32, pcm)), "a.wav", "audio/wav");

        Assert.Equal(44 + 200, result.WaveBytes.Length);
        Assert.Equal((short)Math.Round(0.5f * short.MaxValue), BitConverter.ToInt16(result.WaveBytes, 44));
    }

    [Fact]
    public async Task Normalize_WavWithUnknownChunks_IsTolerated()
    {
        var pcm = new byte[64];
        using var stream = new MemoryStream(BuildWavWithJunkChunk(1, 1, 16000, 16, pcm));

        var normalizer = new AudioNormalizer();
        var result = await normalizer.NormalizeToWavePcm16Mono16KAsync(stream, "a.wav", "audio/wav");

        Assert.Equal(44 + 64, result.WaveBytes.Length);
    }

    [Fact]
    public async Task Normalize_EmptyInput_IsRejected()
    {
        var normalizer = new AudioNormalizer();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            normalizer.NormalizeToWavePcm16Mono16KAsync(new MemoryStream(Array.Empty<byte>()), "a.wav", "audio/wav"));
    }

    [Fact]
    public async Task Normalize_WavWithoutFormatChunk_IsRejected()
    {
        // RIFF/WAVE 头是真的，但没有 fmt chunk —— 必须报错，不能猜。
        var broken = new byte[16];
        WriteAscii(broken, 0, "RIFF");
        BitConverter.TryWriteBytes(broken.AsSpan(4), 8);
        WriteAscii(broken, 8, "WAVE");

        var normalizer = new AudioNormalizer();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            normalizer.NormalizeToWavePcm16Mono16KAsync(new MemoryStream(broken), "a.wav", "audio/wav"));
    }

    [Fact]
    public async Task Normalize_NonWavWithoutFfmpeg_ReportsMissingConverter()
    {
        var mp3 = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        // 指定一个不可能存在的转换器，必须明确告知缺转换器，而不是悄悄产出错格式的 WAV。
        var normalizer = new AudioNormalizer("clapback-ffmpeg-does-not-exist");
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            normalizer.NormalizeToWavePcm16Mono16KAsync(new MemoryStream(mp3), "a.mp3", "audio/mpeg"));

        Assert.Contains("ffmpeg", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- WAV 构造辅助

    private static byte[] BuildWav(
        ushort formatTag,
        int channels,
        int sampleRate,
        int bitsPerSample,
        byte[] data,
        bool dataBeforeFormat = false)
    {
        var blockAlign = channels * (bitsPerSample / 8);
        var headerSize = 44;
        var output = new byte[headerSize + data.Length];

        WriteAscii(output, 0, "RIFF");
        BitConverter.TryWriteBytes(output.AsSpan(4), output.Length - 8);
        WriteAscii(output, 8, "WAVE");

        if (dataBeforeFormat)
        {
            WriteAscii(output, 12, "data");
            BitConverter.TryWriteBytes(output.AsSpan(16), data.Length);
            data.CopyTo(output, 20);
            WriteAscii(output, 20 + data.Length, "fmt ");
            BitConverter.TryWriteBytes(output.AsSpan(24 + data.Length), 16);
            WriteFormat(output.AsSpan(28 + data.Length), formatTag, channels, sampleRate, bitsPerSample, blockAlign);
            return output;
        }

        WriteAscii(output, 12, "fmt ");
        BitConverter.TryWriteBytes(output.AsSpan(16), 16);
        WriteFormat(output.AsSpan(20), formatTag, channels, sampleRate, bitsPerSample, blockAlign);
        WriteAscii(output, 36, "data");
        BitConverter.TryWriteBytes(output.AsSpan(40), data.Length);
        data.CopyTo(output, 44);
        return output;
    }

    private static byte[] BuildWavWithJunkChunk(
        ushort formatTag,
        int channels,
        int sampleRate,
        int bitsPerSample,
        byte[] data)
    {
        var blockAlign = channels * (bitsPerSample / 8);
        // 12 (RIFF/WAVE) + 8+4 (JUNK) + 8+16 (fmt) + 8+data (data)
        var output = new byte[12 + 12 + 24 + 8 + data.Length];

        WriteAscii(output, 0, "RIFF");
        BitConverter.TryWriteBytes(output.AsSpan(4), output.Length - 8);
        WriteAscii(output, 8, "WAVE");

        var offset = 12;
        WriteAscii(output, offset, "JUNK");
        BitConverter.TryWriteBytes(output.AsSpan(offset + 4), 4);
        offset += 12;

        WriteAscii(output, offset, "fmt ");
        BitConverter.TryWriteBytes(output.AsSpan(offset + 4), 16);
        WriteFormat(output.AsSpan(offset + 8), formatTag, channels, sampleRate, bitsPerSample, blockAlign);
        offset += 24;

        WriteAscii(output, offset, "data");
        BitConverter.TryWriteBytes(output.AsSpan(offset + 4), data.Length);
        data.CopyTo(output, offset + 8);
        return output;
    }

    private static void WriteFormat(
        Span<byte> target,
        ushort formatTag,
        int channels,
        int sampleRate,
        int bitsPerSample,
        int blockAlign)
    {
        BitConverter.TryWriteBytes(target, formatTag);
        BitConverter.TryWriteBytes(target[2..], (ushort)channels);
        BitConverter.TryWriteBytes(target[4..], sampleRate);
        BitConverter.TryWriteBytes(target[8..], sampleRate * blockAlign);
        BitConverter.TryWriteBytes(target[12..], (ushort)blockAlign);
        BitConverter.TryWriteBytes(target[14..], (ushort)bitsPerSample);
    }

    private static void WriteAscii(byte[] target, int offset, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            target[offset + i] = (byte)text[i];
        }
    }
}
