using ClapBack.Core.Models;

namespace ClapBack.Core.Abstractions;

public interface IAudioRecorder
{
    bool IsRecording { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task<RecordedAudio> StopAsync(CancellationToken cancellationToken = default);
    Task CancelAsync(CancellationToken cancellationToken = default);
}
