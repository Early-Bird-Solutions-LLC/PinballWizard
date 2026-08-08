using Azure;
using Azure.AI.DocumentIntelligence;
using PinballWizard.Domain.Abstractions;

namespace PinballWizard.Processor.Pipeline;

public sealed class PdfExtractor : IContentExtractor
{
    private readonly DocumentIntelligenceClient _client;

    public PdfExtractor(DocumentIntelligenceClient client)
    {
        _client = client;
    }

    public string Name => "PdfExtractor";

    public bool CanExtract(string mimeType, string fileExtension)
        => mimeType is "application/pdf" || fileExtension is ".pdf";

    public async Task<ExtractionResult> ExtractAsync(Stream content, string filename, CancellationToken ct = default)
    {
        var binaryData = await BinaryData.FromStreamAsync(content, ct);

        var operation = await _client.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            new AnalyzeDocumentOptions("prebuilt-layout", binaryData),
            ct);

        var result = operation.Value;
        var sections = new List<TextSection>();
        var fullText = new System.Text.StringBuilder();

        ExtractParagraphs(result, sections, fullText);
        ExtractTables(result, sections, fullText);

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

    private static void ExtractParagraphs(AnalyzeResult result, List<TextSection> sections, System.Text.StringBuilder fullText)
    {
        if (result.Paragraphs is not { Count: > 0 })
            return;

        string? currentHeading = null;
        int currentLevel = 0;

        foreach (var paragraph in result.Paragraphs)
        {
            int? pageNumber = GetPageNumber(paragraph.BoundingRegions);

            if (paragraph.Role == ParagraphRole.SectionHeading || paragraph.Role == ParagraphRole.Title)
            {
                currentHeading = paragraph.Content;
                currentLevel = paragraph.Role == ParagraphRole.Title ? 1 : 2;
            }

            sections.Add(new TextSection
            {
                Content = paragraph.Content,
                Heading = currentHeading,
                Level = currentLevel,
                PageNumber = pageNumber
            });

            fullText.AppendLine(paragraph.Content);
        }
    }

    private static void ExtractTables(AnalyzeResult result, List<TextSection> sections, System.Text.StringBuilder fullText)
    {
        if (result.Tables is not { Count: > 0 })
            return;

        foreach (var table in result.Tables)
        {
            var tableContent = FormatTable(table);
            sections.Add(new TextSection
            {
                Content = tableContent,
                Heading = "Table",
                Level = 0,
                PageNumber = GetPageNumber(table.BoundingRegions)
            });
            fullText.AppendLine(tableContent);
        }
    }

    private static string FormatTable(DocumentTable table)
    {
        var tableText = new System.Text.StringBuilder();
        int currentRow = -1;

        foreach (var cell in table.Cells)
        {
            if (cell.RowIndex != currentRow)
            {
                if (currentRow >= 0) tableText.AppendLine();
                currentRow = cell.RowIndex;
            }
            else
            {
                tableText.Append(" | ");
            }
            tableText.Append(cell.Content);
        }

        return tableText.ToString();
    }

    private static int? GetPageNumber(IReadOnlyList<BoundingRegion>? regions)
        => regions is { Count: > 0 } ? regions[0].PageNumber : null;
}
