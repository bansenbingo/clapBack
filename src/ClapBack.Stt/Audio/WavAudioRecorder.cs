using System.IO;
using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;
using NAudio.Wave;
using NAudio.Utils;

namespace ClapBack.Stt.Audio;

/// <summary>
/// 用 NAudio / WASAPI 录成 16kHz 单声道 PCM WAV，输出在内存里（不落盘）。
///
/// 设备下标由 <c>Func&lt;int&gt;</c> 注入，通常来自
/// <see cref="IAudioInputDeviceCatalog.ResolveIndex"/>：<c>ClapBackSettings.MicrophoneDeviceId</c>
/// 存的是设备名，注册开始录音时才解析成下标。
/// </summary>
public sealed class WavAudioRecorder : IAudioRecorder
{
    private readonly object _sync = new();
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private MemoryStream? _buffer;
    private TaskCompletionSource<RecordedAudio>? _completion;
    private readonly Func<int> _inputDeviceNumber;

    public WavAudioRecorder(Func<int>? inputDeviceNumber = null)
    {
        _inputDeviceNumber = inputDeviceNumber ?? (() => 0);
    }

    public bool IsRecording
    {
        get
        {
            lock (_sync)
            {
                return _waveIn is not null;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_waveIn is not null)
            {
                throw new InvalidOperationException("已经在录音");
            }

            _buffer = new MemoryStream();
            // Dispose finalizes the WAV header; keep the buffer open until it is copied.
            _writer = new WaveFileWriter(new IgnoreDisposeStream(_buffer), new WaveFormat(16_000, 16, 1));
            _completion = new TaskCompletionSource<RecordedAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waveIn = new WaveInEvent
            {
                DeviceNumber = Math.Max(0, _inputDeviceNumber()),
                WaveFormat = new WaveFormat(16_000, 16, 1),
                BufferMilliseconds = 50
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            try
            {
                _waveIn.StartRecording();
            }
            catch
            {
                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.RecordingStopped -= OnRecordingStopped;
                _waveIn.Dispose();
                _waveIn = null;
                _writer.Dispose();
                _writer = null;
                _buffer.Dispose();
                _buffer = null;
                _completion = null;
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public async Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default)
    {
        Task<RecordedAudio> completion;
        lock (_sync)
        {
            if (_waveIn is null || _completion is null)
            {
                throw new InvalidOperationException("当前没有正在进行的录音");
            }

            completion = _completion.Task;
            _waveIn.StopRecording();
        }

        return await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        Task<RecordedAudio>? completion = null;
        lock (_sync)
        {
            if (_waveIn is not null)
            {
                completion = _completion?.Task;
                _waveIn.StopRecording();
            }
        }

        if (completion is not null)
        {
            try
            {
                using var discarded = await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Cancellation cleanup is best effort.
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_sync)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
            _writer?.Flush();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        TaskCompletionSource<RecordedAudio>? completion;
        RecordedAudio? result = null;
        Exception? error = e.Exception;
        lock (_sync)
        {
            completion = _completion;
            try
            {
                // Finalize the RIFF/data lengths before copying the bytes. The
                // IgnoreDisposeStream wrapper leaves the memory buffer readable.
                _writer?.Dispose();
                if (error is null && _buffer is not null)
                {
                    var bytes = _buffer.ToArray();
                    result = new RecordedAudio(new MemoryStream(bytes, writable: false), "clapback.wav");
                }
            }
            catch (Exception ex)
            {
                error ??= ex;
            }
            finally
            {
                _writer = null;
                _buffer?.Dispose();
                _buffer = null;
            }
            if (_waveIn is not null)
            {
                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.RecordingStopped -= OnRecordingStopped;
                _waveIn.Dispose();
                _waveIn = null;
            }

            _completion = null;
        }

        if (error is not null)
        {
            completion?.TrySetException(error);
        }
        else if (result is not null)
        {
            completion?.TrySetResult(result);
        }
        else
        {
            completion?.TrySetException(new InvalidOperationException("录音没有产生音频数据"));
        }
    }
}
