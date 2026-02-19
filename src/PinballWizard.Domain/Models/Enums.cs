namespace PinballWizard.Domain.Models;

/// <summary>
/// Where the document was discovered.
/// </summary>
public enum SourceType
{
    // Stern Pinball (original sources)
    ManualsPage,
    GamePage,
    ServiceBulletinPage,

    // API-based sources
    OpdbApi,
    PinballMapApi,
    IfpaApi,

    // Community sources
    TiltForums,
    PinWiki,
    PinballArchive,

    // Static HTML sources
    ClaysRepairGuides,
    InternetArchive,
    ArcadeManualArchive,

    // Reference & strategy sources
    WikipediaGlossary,
    StrategyGuide,

    // Manufacturer support sites (non-Stern)
    ManufacturerSite,

    // Rich media
    YouTubeChannel
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

    // New types for expanded sources
    Rulesheet,
    WikiArticle,
    RepairGuide,
    StrategyGuide,
    Glossary,
    MachineRecord,
    LocationRecord,

    Other
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
    Promotional,

    // New categories for expanded sources
    GameRules,
    Strategy,
    History,
    Glossary,
    Pricing,
    Location,
    Repair,
    Troubleshooting,
    Restoration,
    Modifications,
    CompetitivePlay
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
    ViewImage,
    ApiResponse,
    WikiPage,
    ForumPost
}
