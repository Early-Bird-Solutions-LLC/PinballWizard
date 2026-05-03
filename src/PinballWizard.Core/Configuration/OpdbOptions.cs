using System.ComponentModel.DataAnnotations;

namespace PinballWizard.Core.Configuration;

/// <summary>
/// Configuration for the OPDB (Open Pinball Database) integration —
/// the canonical machine catalog the project uses to identify pinball
/// machines across all manufacturer sources.
/// </summary>
/// <remarks>
/// Phase 1.1 of the parallel execution plan. OPDB is API-based (not a
/// site scraper), so it is the lowest-risk first integration on the
/// Clean Architecture layout — validates the layout cleanly
/// accommodates non-Stern sources before per-manufacturer scrapers
/// start landing.
/// <para>
/// API token: get one at https://opdb.org/api by registering. The
/// token is a simple bearer credential and MUST NOT be committed.
/// Set it via environment variable <c>OPDB__APITOKEN</c> (the double
/// underscore is the .NET configuration convention for nested keys),
/// the user-secrets store in development, or an Azure Key Vault
/// secret in production (referenced through Key Vault Configuration
/// Provider when Phase 4 lands).
/// </para>
/// </remarks>
public sealed class OpdbOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Opdb";

    /// <summary>
    /// Full configuration key for <see cref="BaseUrl"/>. Exposed so
    /// callers (e.g., the CLI's gating logic that decides whether to
    /// register the OPDB integration) can presence-check the key
    /// without duplicating the <c>"Opdb:BaseUrl"</c> string and risking
    /// a silent drift if the section is ever renamed.
    /// </summary>
    public const string BaseUrlKey = $"{SectionName}:{nameof(BaseUrl)}";

    /// <summary>OPDB API base URL. Defaults to the public production endpoint.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = "https://opdb.org/api/";

    /// <summary>
    /// Bearer token for OPDB API requests. Required for any non-public
    /// endpoint. Empty string is allowed during local-only sync runs
    /// against the freely-available <c>/api/changelog</c> endpoint, but
    /// is rejected for any sync that touches the machines list.
    /// </summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// Maximum number of machines fetched per page. OPDB caps this; we
    /// pick 100 by default which matches the documented page limit.
    /// </summary>
    [Range(1, 1000)]
    public int PageSize { get; set; } = 100;

    /// <summary>
    /// Per-request HTTP timeout in seconds. OPDB is generally fast but
    /// the changelog and machines-list endpoints occasionally take a
    /// few seconds for the largest pages.
    /// </summary>
    [Range(5, 600)]
    public int HttpTimeoutSeconds { get; set; } = 60;
}
