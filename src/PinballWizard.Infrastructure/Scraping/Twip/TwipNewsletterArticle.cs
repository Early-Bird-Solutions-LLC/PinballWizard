namespace PinballWizard.Infrastructure.Scraping.Twip;

/// <summary>
/// A parsed TWIP newsletter article ready for synthesis and RAG indexing.
/// Produced by <see cref="TwipNewsletterClient.FetchArticleAsync"/>.
/// </summary>
public sealed class TwipNewsletterArticle
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required string CanonicalUrl { get; init; }
    public string Author { get; init; } = "Colin Alsheimer";
    public DateTimeOffset? PublishedAt { get; init; }
    public required string BodyText { get; init; }
}
