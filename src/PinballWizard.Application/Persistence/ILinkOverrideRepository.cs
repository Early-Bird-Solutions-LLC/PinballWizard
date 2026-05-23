using PinballWizard.Core.Models;

namespace PinballWizard.Application.Persistence;

public interface ILinkOverrideRepository
{
    // Load all overrides for startup caching by the linker.
    // In practice < 1,000 records — safe to load eagerly.
    Task<IReadOnlyDictionary<string, LinkOverrideRecord>> LoadAllAsync(CancellationToken cancellationToken);

    // Upsert an admin decision. source_pattern = id = partition key.
    Task UpsertAsync(LinkOverrideRecord record, CancellationToken cancellationToken);

    // Point-read by source_pattern.
    Task<LinkOverrideRecord?> GetAsync(string sourcePattern, CancellationToken cancellationToken);

    // Delete an override (revoke admin decision).
    Task DeleteAsync(string sourcePattern, CancellationToken cancellationToken);
}
