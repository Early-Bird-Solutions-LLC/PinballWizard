namespace PinballWizard.Core.Models;

public static class ScrapeRunId
{
    // Deterministic per-run id: same source can't run twice in one millisecond
    // (runs are serial). Stamped on captured items as run_id AND used as the
    // scrape_runs document id, so a document's run_id == its run record's id.
    public static string For(string sourceId, DateTimeOffset runAt) =>
        $"{sourceId}_{runAt.UtcDateTime:yyyyMMddHHmmssfff}Z";
}
