namespace PinballWizard.Core.Models;

/// <summary>
/// Where the document was discovered.
/// </summary>
public enum SourceType
{
    ManualsPage,
    GamePage,
    ServiceBulletinPage,
    JjpProductPage,
    AmericanPinballGamePage,
    SpookyPinballGamePage,
    PinballBrothersGamePage,
    BarrelsOfFunProductPage,
    ChicagoGamingGamePage,
    MultimorphicProductPage,
}

/// <summary>
/// What kind of document this is.
/// </summary>
public enum DocumentType
{
    Manual,
    Schematic,
    Firmware,
    ServiceBulletin,
    Flyer,
    SpecSheet,
    FeatureMatrix,

    /// <summary>
    /// A standalone gameplay-rules sheet (e.g. "Rules PDF", "Spooky Rules",
    /// "Rulesheet"). Distinct from <c>Manual</c>, which is a comprehensive
    /// operator/owner guide that may contain rules as one chapter alongside
    /// schematics, maintenance, and parts information. A Rulesheet is
    /// rules-only; a Manual can contain a <see cref="ContentCategory.Rules"/>
    /// chapter but is not classified as Rulesheet. Per ADR-0042 this value
    /// is added to <c>RagIngestionOptions.AcceptedDocumentTypes</c> so
    /// gameplay-mechanic questions can be answered from corpus.
    /// </summary>
    Rulesheet,

    Readme,
    Other,

    /// <summary>
    /// Synthesized metadata card produced from a <c>Machine</c> Cosmos
    /// record by Phase 4 W3-1's <c>MetadataCardSynthesizer</c>. Cards
    /// are not PDF-derived; <c>page_start</c> / <c>page_end</c> default
    /// to 0 in the index. Per ADR-0021, this enum value projects to the
    /// snake-case index value <c>metadata_card</c>.
    /// </summary>
    MetadataCard,

    /// <summary>
    /// Synthesized long-form game-overview card built from a Machine's
    /// OverviewProse + per-edition sections by GameOverviewSynthesizer.
    /// Per the index contract, projects via .ToString() to "GameOverview";
    /// the read-side snake_case alias is "game_overview".
    /// </summary>
    GameOverview,
}

/// <summary>
/// Content categories found within a document (a single manual can contain many).
/// </summary>
public enum ContentCategory
{
    Rules,
    Schematics,
    PartsList,
    Wiring,
    Diagnostics,
    Assembly,
    Maintenance,
    Firmware,
    Specifications,
    Promotional
}

/// <summary>
/// Which tab on a game page the file was found under.
/// </summary>
public enum GamePageTab
{
    PromotionalMaterials,
    GameCode,
    SpecsAndManual
}

/// <summary>
/// How the link appeared on the page.
/// </summary>
public enum ActionType
{
    OpenPdf,
    DownloadFile,
    ExternalLink,
    ViewImage
}