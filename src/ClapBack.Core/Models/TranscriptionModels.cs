namespace ClapBack.Core.Models;

public sealed record TranscriptionOptions(string Model, string Language);

public sealed record TranscriptResult(string Text, TimeSpan? Duration = null);

public sealed class RecordedAudio : IDisposable
{
    public RecordedAudio(Stream content, string fileName, string contentType = "audio/wav")
    {
        Content = content;
        FileName = fileName;
        ContentType = contentType;
    }

    public Stream Content { get; }
    public string FileName { get; }
    public string ContentType { get; }

    public void Dispose() => Content.Dispose();
}
