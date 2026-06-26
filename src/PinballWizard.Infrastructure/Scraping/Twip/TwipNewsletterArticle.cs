namespace PinballWizard.Infrastructure.Scraping.Twip;

// A parsed TWIP newsletter article ready for synthesis and RAG indexing.
// Produced by TwipNewsletterClient.FetchArticleAsync.
public sealed record TwipNewsletterArticle
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required string CanonicalUrl { get; init; }
    public string Author { get; init; } = "Colin Alsheimer";
    public DateTimeOffset? PublishedAt { get; init; }
    public required string BodyText { get; init; }
}
