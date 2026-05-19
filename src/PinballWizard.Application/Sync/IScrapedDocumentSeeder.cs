namespace PinballWizard.Application.Sync;

public interface IScrapedDocumentSeeder
{
    Task<ScrapedDocumentSeedResult> SeedAsync(string catalogPath, CancellationToken cancellationToken);
}

public sealed record ScrapedDocumentSeedResult
{
    public int Upserted { get; set; }
    public int Skipped { get; set; }
}
