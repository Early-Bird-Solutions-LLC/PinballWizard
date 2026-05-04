namespace PinballWizard.Application.Sync;

public interface IIngestionSourceSeeder
{
    Task<IngestionSourceSeedResult> SeedAsync(string manifestPath, CancellationToken cancellationToken);
}

public sealed record IngestionSourceSeedResult
{
    public required int Inserted { get; init; }

    public required int Updated { get; init; }

    public required int Total { get; init; }
}
