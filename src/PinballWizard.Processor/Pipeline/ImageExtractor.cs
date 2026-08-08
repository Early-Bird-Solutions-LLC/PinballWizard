using Azure;
using Azure.AI.DocumentIntelligence;
using PinballWizard.Domain.Abstractions;

namespace PinballWizard.Processor.Pipeline;

public sealed class ImageExtractor : IContentExtractor
{
    private readonly DocumentIntelligenceClient _client;

    public ImageExtractor(DocumentIntelligenceClient client)
    {
        _client = client;
    }

    public string Name => "ImageExtractor";

    public bool CanExtract(string mimeType, string fileExtension)
        => mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || fileExtension is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".tiff" or ".tif";

    public async Task<ExtractionResult> ExtractAsync(Stream content, string filename, CancellationToken ct = default)
    {
        var binaryData = await BinaryData.FromStreamAsync(content, ct);

        var operation = await _client.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            new AnalyzeDocumentOptions("prebuilt-read", binaryData),
            ct);

        var result = operation.Value;
        var sections = new List<TextSection>();
        var fullText = new System.Text.StringBuilder();

        if (result.Pages is { Count: > 0 })
        {
            foreach (var page in result.Pages)
            {
                if (page.Lines is null) continue;

                foreach (var line in page.Lines)
                {
                    if (!string.IsNullOrWhiteSpace(line.Content))
                    {
                        sections.Add(new TextSection
                        {
                            Content = line.Content,
                            Heading = null,
                            Level = 0,
                            PageNumber = page.PageNumber
                        });
                        fullText.AppendLine(line.Content);
                    }
                }
            }
        }

        return new ExtractionResult
        {
            Text = fullText.ToString(),
            Sections = sections,
            PageCount = result.Pages?.Count,
            Metadata = new Dictionary<string, string>
            {
                ["extractor"] = Name,
                ["filename"] = filename
            }
        };
    }
}
