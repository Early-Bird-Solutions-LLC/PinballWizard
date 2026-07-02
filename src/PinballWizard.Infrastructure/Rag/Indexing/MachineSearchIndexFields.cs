namespace PinballWizard.Infrastructure.Rag.Indexing;

// Canonical field name constants for the machine search index (ADR-0049).
// Single source of truth used by both the schema builder
// (MachineSearchIndexSchema — which defines the fields) and the document
// class (MachineSearchDocument — which serializes into them). Mirrors the
// AiSearchIndexFields pattern used by the corpus index (retrieval-side
// constants), but scoped to the machine findability index.
//
// Phase 2b's query code will import these same constants to name the
// fields it targets at query time — keeping schema definition and
// query field references in sync mechanically rather than by convention.
public static class MachineSearchIndexFields
{
    public const string Id              = "id";
    public const string Title           = "title";
    public const string TitlePrefix     = "title_prefix";
    public const string TitlePhonetic   = "title_phonetic";
    public const string Manufacturer    = "manufacturer";
    public const string ManufacturerKey = "manufacturer_key";
    public const string Designers       = "designers";
    public const string Themes          = "themes";
    public const string Year            = "year";
    public const string GroupId         = "group_id";
    public const string EditionLabel    = "edition_label";
    public const string Completeness    = "completeness";
    public const string LastUpdatedUtc  = "last_updated_utc";
}
