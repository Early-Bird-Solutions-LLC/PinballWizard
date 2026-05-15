# Source Site Outage — Scraper 403/429/5xx or robots.txt Change
**Trigger:** Source error rate spike detected in logs, `robots.txt` change detected, or `pinwiz.politeness.*` metrics show sustained failures
**Alert rule:** Manual / DR drill (Phase 6 note: a `pinwiz-alert-scraper-errors` alert rule targeting `pinwiz.opdb.sync.failed` and scrape error logs is a Phase 6 follow-up)
**Time budget:** First 60 minutes
**Last walked:** 2026-05-15 (pre-launch procedure review — steps verified against deployed dev infrastructure; live-incident drill deferred to Phase 7 when real app image is running)

---

## Core rule — polite-by-construction is non-negotiable

Before any step: **do not retry aggressively.** The politeness gate (`IPolitenessGate`) backs off automatically on 429/503. Do not bypass it. Do not set `PolitenessOverrides.MinDelayMs` to 0 or disable robots.txt checking. Polite-by-construction is a showcase marketing surface and a locked invariant (CLAUDE.md § Locked invariants #2).

---

## Step 1 — Identify the affected source (10 min)

Check application logs for scraper errors:

```kql
// Application Insights — Log Analytics workspace
traces
| where timestamp > ago(2h)
| where message contains "scraper" or message contains "politeness" or message contains "robots.txt"
| where severityLevel >= 3  // Warning or above
| project timestamp, message, customDimensions.source, severityLevel
| order by timestamp desc
| take 30
```

Also check for HTTP error patterns:

```kql
exceptions
| where timestamp > ago(2h)
| where outerMessage contains "403" or outerMessage contains "429"
    or outerMessage contains "5xx" or outerMessage contains "robots"
| project timestamp, outerMessage, customDimensions.source
| order by timestamp desc
| take 20
```

Identify the source key (e.g., `stern`, `jjp`, `ap`, `spooky`, `pinballbrothers`, `barrelsoffun`, `multimorphic`, `cgc`, `opdb`).

---

## Step 2 — Check robots.txt for the affected source (5 min)

If the error is a 403 or the logs mention `robots.txt`:

```powershell
# Check robots.txt for the affected source (example: sternpinball.com)
Invoke-WebRequest -Uri "https://sternpinball.com/robots.txt" | Select-Object -ExpandProperty Content
```

**If `Disallow: /` is newly present for the PinballWizard user-agent (or globally):**
- Immediately set `enabled = false` on the `IngestionSource` in Cosmos — do not wait.
- Do NOT re-enable without a yes-response to polite outreach on file (see Step 5).

**If robots.txt is unchanged:** the 403/429/5xx is likely transient. Continue to Step 3.

---

## Step 3 — Check the IngestionSource.enabled flag (5 min)

Check the current state of the source's `IngestionSource` record in Cosmos:

```powershell
# Using the CLI seed/status command — or check via Cosmos Data Explorer
dotnet run --project src/PinballWizard.Cli -- --status --verbose
```

If the source is currently `enabled = true` and robots.txt has a new `Disallow`, update it immediately:

```powershell
# Via the Cosmos Data Explorer (portal):
# Navigate to pinwiz-db → ingestion_sources → find the source document
# Set "enabled": false and save

# Or: edit data/seeds/ingestion_sources.v1.json, set enabled: false,
# then re-seed (idempotent):
dotnet run --project src/PinballWizard.Cli -- --seed-ingestion-sources
```

---

## Step 4 — Verify the politeness gate is backing off correctly (5 min)

For transient errors (429/503), the politeness gate should back off without operator intervention. Verify it is working:

```kql
// Check for politeness gate activity (look for delay/backoff log messages)
traces
| where timestamp > ago(1h)
| where message contains "politeness" or message contains "backoff" or message contains "delay"
| project timestamp, message, customDimensions.source
| order by timestamp desc
| take 20
```

Expected: log entries showing the gate is delaying requests (e.g., "Acquiring politeness token for stern", "Rate limit enforced — waiting Xms"). This means the gate is working as designed.

**Do not lower the delay or increase concurrency** to compensate for a 429. The gate's back-off is the correct response.

If the gate is NOT backing off (scraper is hammering the source despite errors), check `IPerSourcePolitenessResolver` — it should be reading `IngestionSource.PolitenessOverrides` from Cosmos. A Cosmos connectivity issue could cause it to fall back to `DefaultPerSourcePolitenessResolver` with permissive defaults. If Cosmos is unhealthy, fix that first (runbook `01-incident-response.md` → Step 3).

---

## Step 5 — Initiate polite outreach if permission may be revoked (20 min)

If the source has newly blocked the scraper in robots.txt (or sent a 403 that appears to be a deliberate block, not a transient error):

1. Confirm the source is set to `enabled = false` (Step 3).
2. Identify the site operator's contact (check the source site's footer, about page, or privacy policy).
3. Draft an outreach email using the `earlybirdsolutions-outreach` skill: explain PinballWizard's purpose, describe what data is accessed, offer attribution + link-back, and ask for explicit permission.
4. File the outreach attempt in `docs/decision-log.md` (date, site, contact method).
5. **Do not re-enable the source until a yes-response is received and documented.**

Note: Pinside and Dutch Pinball are already deferred indefinitely per their `Disallow: /` policies — do not initiate outreach for these without a fresh policy check.

---

## Step 6 — OPDB-specific handling (5 min)

OPDB (`opdb.org/api/`) is an API source, not a web scraper. If OPDB returns 401 or 403:

- 401 — API token likely expired or revoked. Run `05-secret-rotation.md` § Rotate OPDB API token.
- 403 — Account may be suspended. Check https://opdb.org for status or contact the OPDB team.
- 429 — OPDB rate limit hit. The politeness gate respects `IngestionSource.PolitenessOverrides.MinDelayMs` for OPDB. Increase the delay if the current value is too aggressive:

```powershell
# In data/seeds/ingestion_sources.v1.json, find the opdb entry and increase MinDelayMs
# Then re-seed:
dotnet run --project src/PinballWizard.Cli -- --seed-ingestion-sources
```

---

## Step 7 — Monitor recovery (ongoing)

After the issue is identified and either the source is disabled (robots.txt change) or the transient error is resolving:

```kql
// Watch for error rate returning to zero
exceptions
| where timestamp > ago(1h)
| where outerMessage contains "403" or outerMessage contains "429"
| summarize error_count = count() by bin(timestamp, 5m), source = tostring(customDimensions.source)
| order by timestamp asc
| render timechart
```

For a disabled source: the error rate should drop to zero immediately after `enabled = false` is persisted.

For a transient outage: monitor until the source returns to normal response codes. The politeness gate's back-off will naturally retry at longer intervals — no operator action needed beyond monitoring.

---

## Post-incident

Append a dated entry to `docs/decision-log.md`:
- Source affected, error type (403 / 429 / 5xx / robots.txt change), whether the source was disabled, outreach status (if applicable), and resolution.
- If the source was disabled and re-enabled after outreach: note the yes-response date and the contact who gave permission.
