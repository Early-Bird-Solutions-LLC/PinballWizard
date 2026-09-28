# 0058 — The politeness gate owns HTTP 429

**Status:** Accepted
**Date:** 2026-09-28

## Context

`pinwiz-job-kineticist-sync` failed every weekly run from 2026-08-23 (issue #968). The job log showed two defects and one missing policy.

- The host-wide standard resilience handler (`ServiceDefaults`, `ConfigureHttpClientDefaults`) retried the 429 three times within about 14 seconds. It sits below `IPolitenessGate`, so the gate saw only the last attempt: four requests, one report, no pacing between them.
- `PoliteScraperBase` passed only `Retry-After` delay-seconds to the gate. An HTTP-date `Retry-After` was dropped. The gate slept out the wait inside `ReportResponseAsync` and then returned the 429 to a caller that threw anyway, so the wait was never followed by a request.
- The 429 escaped `--sync-kineticist-tutorials` as an unhandled `HttpRequestException`.

Probing on 2026-09-28 found that the response is not a rate limit. It is a Vercel Security Checkpoint: HTTP 429, `x-vercel-mitigated: challenge`, no `Retry-After`. It is served on the category listing and on article `.md` pages. The robots.txt has no `Crawl-delay` and advertises `/sitemap.xml`. The news sitemap it points to is still served.

## Decision

1. **429 is reported to the gate, never retried below it.** Clients that route through the gate use `AddPoliteResilienceHandler`. It replaces the inherited pipeline with a standard handler whose retry predicate is `IsTransient && status != 429`. 5xx, 408, network errors, and attempt timeouts still retry there.
2. **The gate turns a 429 into a per-origin backoff.** `Retry-After` is honored in both forms (`RateLimitSignals.GetRetryAfter`). An HTTP-date is measured against the response's own `Date` header. Without a `Retry-After`, the backoff is `RateLimitBackoffMs × 2^(streak−1)`, capped at `MaxRetryAfterSeconds`. The next `AcquireForRequestAsync` for that origin waits it out. The streak is per origin, as the `Max429Streak` doc already said, so a healthy origin does not reset a throttled one.
3. **The retry budget is the per-source policy.** `PoliteScraperBase` re-sends a body-less request after a 429 through a fresh lease. The gate throws `TooMany429Responses` when the streak exceeds `Max429Streak`. It throws `RetryAfterExceedsBudget` when a server asks for more than `MaxRetryAfterSeconds`: the run fails instead of sleeping for hours. `Max429StreakUpperBound` is the hard cap.
4. **A bot challenge is not a rate limit.** A non-success response carrying `x-vercel-mitigated: challenge` or `cf-mitigated: challenge` is reported, then fails with `BotChallenge` without a retry. Waiting cannot pass a challenge, so a retry would only add load.
5. **Per-source pacing lives in the ingestion-source seed.** `RateLimitBackoffMs` and `MaxRetryAfterSeconds` join `PolitenessOverrides`. `kineticist_tutorials` is set to 5 s between requests and a 60 s base backoff, and `--seed-ingestion-sources` applies it.
6. **Kineticist discovery reads the news sitemap** (`/sitemap/news.xml`, one cached request). It no longer pages the rendered category listing.

`--sync-kineticist-tutorials` moved to `SyncKineticistTutorialsCommand`. A politeness or HTTP failure during discovery or mid-run exits 1 with a `fail:` log that names the URL and the reason. Zero discovered tutorials or zero indexed tutorials also exits 1.

## Consequences

**Positive:**

- Every 429 the site sends is counted and paced by the gate. The logs show the backoff basis (`Retry-After` or policy) and the time of the next request.
- A persistent refusal fails after a known, per-source budget. A job that indexed nothing is red, not green.
- The Kineticist job still fails today, but it fails visibly with remediation text ("ask the operator to allow the crawler") instead of a stack trace.

**Negative:**

- Only the Kineticist tutorials client uses the polite pipeline in this change. Other gated clients still inherit the host handler that retries 429, until they adopt `AddPoliteResilienceHandler`.
- Backoff waits happen inside the lease acquire, so a run that meets a real rate limit takes minutes longer. That cost is intended.
- The challenge markers are vendor headers (Vercel, Cloudflare). A different bot-protection vendor looks like a plain 429 and uses the 429 budget.

## Alternatives considered

- **Keep Polly's 429 retry and raise its delay.** It still runs below the gate: it is not counted, not per origin, and not configurable per source. Rejected.
- **Disable the job schedule.** This hides the failure on the dashboard, and the job still has a working, visible failure path. The allowance request is an operator follow-up (issue #968).
- **Rotate the User-Agent or imitate a browser to pass the checkpoint.** This defeats the operator's control. It contradicts the polite-by-construction invariant and the permission in ADR-0043. Rejected.
