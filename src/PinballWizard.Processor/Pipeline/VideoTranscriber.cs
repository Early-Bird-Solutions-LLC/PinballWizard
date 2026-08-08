using System.Text;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.Extensions.Options;
using PinballWizard.Domain.Abstractions;

namespace PinballWizard.Processor.Pipeline;

public sealed class VideoTranscriber : IContentExtractor
{
    private readonly ProcessorSettings _settings;

    public VideoTranscriber(IOptions<ProcessorSettings> settings)
    {
        _settings = settings.Value;
    }

    public string Name => "VideoTranscriber";

    public bool CanExtract(string mimeType, string fileExtension)
        => mimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
        || mimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        || fileExtension is ".mp4" or ".mp3" or ".wav" or ".webm" or ".m4a" or ".ogg";

    public async Task<ExtractionResult> ExtractAsync(Stream content, string filename, CancellationToken ct = default)
    {
        // Write stream to a temp file for the Speech SDK (requires file-based input)
        var tempPath = Path.Combine(Path.GetTempPath(), $"pinwiz_{Guid.NewGuid()}{Path.GetExtension(filename)}");
        try
        {
            await using (var fs = File.Create(tempPath))
            {
                await content.CopyToAsync(fs, ct);
            }

            var speechConfig = SpeechConfig.FromEndpoint(new Uri(_settings.SpeechEndpoint));
            speechConfig.SpeechRecognitionLanguage = "en-US";
            speechConfig.SetProperty(PropertyId.SpeechServiceResponse_RequestWordLevelTimestamps, "true");

            using var audioConfig = AudioConfig.FromWavFileInput(tempPath);
            using var recognizer = new SpeechRecognizer(speechConfig, audioConfig);

            var sections = new List<TextSection>();
            var fullText = new StringBuilder();
            var tcs = new TaskCompletionSource<bool>();

            recognizer.Recognized += (_, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
                {
                    var offsetSeconds = (int)(e.Result.OffsetInTicks / TimeSpan.TicksPerSecond);
                    var timestamp = TimeSpan.FromSeconds(offsetSeconds).ToString(@"hh\:mm\:ss");

                    sections.Add(new TextSection
                    {
                        Content = e.Result.Text,
                        Heading = $"Timestamp {timestamp}",
                        Level = 0
                    });
                    fullText.AppendLine(e.Result.Text);
                }
            };

            recognizer.SessionStopped += (_, _) => tcs.TrySetResult(true);
            recognizer.Canceled += (_, e) =>
            {
                if (e.Reason == CancellationReason.Error)
                    tcs.TrySetException(new InvalidOperationException($"Speech recognition error: {e.ErrorDetails}"));
                else
                    tcs.TrySetResult(true);
            };

            await recognizer.StartContinuousRecognitionAsync();

            // Wait for completion or cancellation
            using var registration = ct.Register(() => tcs.TrySetCanceled());
            await tcs.Task;

            await recognizer.StopContinuousRecognitionAsync();

            return new ExtractionResult
            {
                Text = fullText.ToString(),
                Sections = sections,
                Metadata = new Dictionary<string, string>
                {
                    ["extractor"] = Name,
                    ["filename"] = filename
                }
            };
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }
}
