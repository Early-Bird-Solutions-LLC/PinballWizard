using MudBlazor;

namespace PinballWizard.Web.Components.Shared;

/// Single source of truth for document link-status → MudBlazor Color.
/// Handles both the PascalCase enum form (LinkStatus.ToString(), e.g. "NotInCatalog")
/// and the snake_case Cosmos-stored form (e.g. "not_in_catalog").
/// Enforces the closed 5-role palette
/// (docs/superpowers/specs/2026-07-07-admin-consistency-design.md §4.1):
/// amber is interactive-only and is never a status color.
internal static class DocumentLinkStatusColor
{
    internal static Color For(string? status) => status switch
    {
        "linked" or "Linked"
            or "manually_linked" or "ManuallyLinked" => Color.Success,
        "failed" or "Failed"
            or "not_in_catalog" or "NotInCatalog"    => Color.Error,
        "platform_generic" or "PlatformGeneric"      => Color.Default, // non-status tag → neutral
        // needs_review: admin-queue signal — informational, not an error.
        // Public surfaces treat NeedsReview the same as NotInCatalog (no machine link,
        // LinkStatus null for unauthenticated viewers — see StreamDocumentsAsync).
        "needs_review" or "NeedsReview"              => Color.Info,
        _                                            => Color.Default,
    };
}
