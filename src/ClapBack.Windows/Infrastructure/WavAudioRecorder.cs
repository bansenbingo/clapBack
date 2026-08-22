using ClapBack.Core.Abstractions;
using ClapBack.Core.Models;
using NAudio.Wave;

namespace ClapBack.Windows.Infrastructure;

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
            _writer = new WaveFileWriter(_buffer, new WaveFormat(16_000, 16, 1));
            _completion = new TaskCompletionSource<RecordedAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waveIn = new WaveInEvent
            {
                DeviceNumber = Math.Max(0, _inputDeviceNumber()),
                WaveFormat = new WaveFormat(16_000, 16, 1),
                BufferMilliseconds = 50
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();
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
        Task? completion = null;
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
                using var audio = await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            if (error is null && _buffer is not null && _writer is not null)
            {
                _writer.Flush();
                _buffer.Position = 0;
                var bytes = _buffer.ToArray();
                result = new RecordedAudio(new MemoryStream(bytes, writable: false), "clapback.wav");
                _buffer.Dispose();
                _buffer = null;
            }

            _writer?.Dispose();
            _writer = null;
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
