using ClapBack.Core.Models;

namespace ClapBack.Core.Abstractions;

public interface ITranscriptionProvider
{
    Task<TranscriptResult> TranscribeAsync(
        Stream audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken = default);
}
