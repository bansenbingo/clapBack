using ClapBack.Core.Models;
using ClapBack.Stt;

if (args.Length != 1)
{
    Console.Error.WriteLine("用法：dotnet run --project src/ClapBack.MacExperiment -- /path/to/audio.wav");
    return 2;
}

var settings = new ClapBackSettings();
var provider = new MacOsFunAsrTranscriptionProvider(() => settings);
await using var audio = File.OpenRead(args[0]);
var result = await provider.TranscribeAsync(
    audio,
    new TranscriptionOptions(settings.Model, settings.Language));
Console.WriteLine(result.Text);
return result.Text.Length == 0 ? 1 : 0;
