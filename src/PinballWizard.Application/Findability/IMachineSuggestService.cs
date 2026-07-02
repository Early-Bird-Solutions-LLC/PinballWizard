namespace PinballWizard.Application.Findability;

// Typeahead suggest service for the public machine findability surface (ADR-0049 phase 3).
//
// Returns a ranked, edition-collapsed list of MachineSuggestion items for a partial
// query against the AI Search machine index.
//
// Contract:
//   - Queries with fewer than 2 non-whitespace characters return empty immediately
//     (no index I/O — keeps the index free from single-character floods).
//   - When the index is not configured the service returns empty (honest degrade,
//     never fabricated results — invariant #17).
//   - Edition collapse: each distinct machine appears once. Editions sharing an
//     OPDB GroupId are collapsed to the top-ranked entry; ungrouped machines are
//     deduped by (Title, Manufacturer). Ranking is purely index-intrinsic
//     (BM25 + machine-content-intrinsic scoring profile) — no engagement signals.
public interface IMachineSuggestService
{
    Task<IReadOnlyList<MachineSuggestion>> SuggestAsync(
        string query,
        int top,
        CancellationToken cancellationToken);
}
