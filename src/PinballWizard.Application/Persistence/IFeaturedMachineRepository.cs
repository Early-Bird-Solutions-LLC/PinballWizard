using PinballWizard.Application.Landing;
using PinballWizard.Core.Domain;

namespace PinballWizard.Application.Persistence;

/// <summary>
/// Repository for the <see cref="FeaturedMachineDocument"/> container per
/// <see href="../../../docs/adr/0025-cosmos-for-user-delight.md">ADR-0025 § 4</see>.
/// Backs the landing-page hero/featured strip served by
/// <see cref="ILandingService.GetLandingAsync"/>.
/// </summary>
/// <remarks>
/// The curated set is small (~6 entries) and read by slug (point-read per
/// document, not a cross-partition query) so the hot-path user-facing call
/// in <c>GetAllAsync</c> is a fixed-cost set of point-reads rather than a
/// fan-out query. Write path is limited to the <c>--seed-featured-machines</c>
/// CLI verb (operator-driven, not user-driven).
/// </remarks>
public interface IFeaturedMachineRepository : IRepository<FeaturedMachineDocument>
{
    /// <summary>
    /// Returns all featured machines ordered by
    /// <see cref="FeaturedMachineDocument.DisplayOrder"/> ascending. Each
    /// document is fetched by a point-read (id == slug == partition-key
    /// value). Returns an empty list when the container has no documents
    /// (not an error — the operator simply hasn't run
    /// <c>--seed-featured-machines</c> yet).
    ///
    /// The result is projected to <see cref="FeaturedMachine"/> (the
    /// Application-layer DTO used by <see cref="LandingResponse"/>) so
    /// callers do not need a reference to the Infrastructure persistence
    /// namespace.
    /// </summary>
    Task<IReadOnlyList<FeaturedMachine>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Returns all raw <see cref="FeaturedMachineDocument"/> records.
    /// Used by the seed verb to check for existing entries and by
    /// integration tests that need to inspect the raw Cosmos shape.
    /// Ordered by <see cref="FeaturedMachineDocument.DisplayOrder"/> ascending.
    /// </summary>
    Task<IReadOnlyList<FeaturedMachineDocument>> GetAllDocumentsAsync(CancellationToken cancellationToken);
}
