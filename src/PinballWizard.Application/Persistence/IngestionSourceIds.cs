namespace PinballWizard.Application.Persistence;

// Canonical IngestionSource document ids — single source of truth for the
// keys used in `data/seeds/ingestion_sources.v1.json` and as the
// `sourceId` argument to `IIngestionSourceRepository.RecordRunResultAsync`.
// Each new scraper adds a constant here in the same PR that adds its
// service implementation; the seed manifest references the same value.
//
// Why a constant set instead of an enum: the underlying type is a string
// (Cosmos document id) and IngestionSource.ScraperImplKey is also a string.
// An enum would require ToString() conversions at every call site without
// adding type safety beyond what the const already gives.
public static class IngestionSourceIds
{
    public const string Opdb = "opdb";
    public const string PinballMap = "pinballmap";
    public const string Stern = "stern";
    public const string Jjp = "jjp";
    public const string Ap = "ap";
    public const string ApBulletins = "ap_bulletins";
    public const string Spooky = "spooky";
    public const string PinballBrothers = "pinballbrothers";
    public const string BarrelsOfFun = "barrelsoffun";
    public const string Multimorphic = "multimorphic";
    public const string Cgc = "cgc";
}
