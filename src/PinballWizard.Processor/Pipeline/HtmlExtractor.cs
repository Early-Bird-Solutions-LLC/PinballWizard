using AngleSharp;
using AngleSharp.Dom;
using PinballWizard.Domain.Abstractions;

namespace PinballWizard.Processor.Pipeline;

public sealed class HtmlExtractor : IContentExtractor
{
    private static readonly HashSet<string> ExcludedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "nav", "footer", "header", "aside", "script", "style", "noscript", "iframe"
    };

    private static readonly HashSet<string> AdClassKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ad", "ads", "advert", "advertisement", "banner", "sidebar", "cookie", "popup", "modal"
    };

    public string Name => "HtmlExtractor";

    public bool CanExtract(string mimeType, string fileExtension)
        => mimeType is "text/html" or "application/xhtml+xml" || fileExtension is ".html" or ".htm";

    public async Task<ExtractionResult> ExtractAsync(Stream content, string filename, CancellationToken ct = default)
    {
        var config = Configuration.Default;
        var context = BrowsingContext.New(config);
        var document = await context.OpenAsync(req => req.Content(content), ct);

        var sections = new List<TextSection>();
        var fullText = new System.Text.StringBuilder();

        // Try to find the main content area
        var body = document.QuerySelector("article")
            ?? document.QuerySelector("main")
            ?? document.QuerySelector("[role='main']")
            ?? document.Body;

        if (body is null)
        {
            return new ExtractionResult
            {
                Text = string.Empty,
                Sections = [],
                Metadata = new Dictionary<string, string>
                {
                    ["extractor"] = Name,
                    ["filename"] = filename
                }
            };
        }

        string? currentHeading = null;
        int currentLevel = 0;

        ExtractNodes(body, sections, fullText, ref currentHeading, ref currentLevel);

        return new ExtractionResult
        {
            Text = fullText.ToString(),
            Sections = sections,
            Metadata = new Dictionary<string, string>
            {
                ["extractor"] = Name,
                ["filename"] = filename,
                ["title"] = document.Title ?? string.Empty
            }
        };
    }

    private static void ExtractNodes(
        INode node,
        List<TextSection> sections,
        System.Text.StringBuilder fullText,
        ref string? currentHeading,
        ref int currentLevel)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IElement element)
            {
                if (ShouldExclude(element)) continue;

                var tagName = element.TagName.ToUpperInvariant();

                if (tagName is "H1" or "H2" or "H3" or "H4" or "H5" or "H6")
                {
                    currentHeading = element.TextContent.Trim();
                    currentLevel = tagName[1] - '0';

                    if (!string.IsNullOrWhiteSpace(currentHeading))
                    {
                        sections.Add(new TextSection
                        {
                            Content = currentHeading,
                            Heading = currentHeading,
                            Level = currentLevel
                        });
                        fullText.AppendLine(currentHeading);
                    }
                    continue;
                }

                if (tagName is "P" or "LI" or "TD" or "TH" or "BLOCKQUOTE" or "PRE" or "DD" or "DT")
                {
                    var text = element.TextContent.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sections.Add(new TextSection
                        {
                            Content = text,
                            Heading = currentHeading,
                            Level = currentLevel
                        });
                        fullText.AppendLine(text);
                    }
                    continue;
                }

                // Recurse into container elements
                ExtractNodes(element, sections, fullText, ref currentHeading, ref currentLevel);
            }
        }
    }

    private static bool ShouldExclude(IElement element)
    {
        if (ExcludedTags.Contains(element.TagName))
            return true;

        var classNames = element.ClassName;
        if (classNames is not null)
        {
            foreach (var keyword in AdClassKeywords)
            {
                if (classNames.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }
}
