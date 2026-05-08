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
