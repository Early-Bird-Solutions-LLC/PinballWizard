# HTTP Resilience — Research and Recommendation

*Researched 2026-05-01 against .NET 10 / Microsoft docs as of 2026-03-30.*

> **Status (2026-05-04): research, not adopted as-is.** What actually shipped under `ServiceDefaults.AddServiceDefaults` is `AddStandardResilienceHandler` registered globally via `ConfigureHttpClientDefaults`, not the per-client custom `AddResilienceHandler` pipeline this doc recommends in the TL;DR. The standard handler's defaults were bumped from `30s/10s` (total/attempt) to `120s/50s` with `CircuitBreaker.SamplingDuration = 120s` to accommodate two endpoint classes that exceed the original Microsoft-default budgets:
>
> - OPDB `/api/export` — bulk catalog response (~2.4 MB / ~2,360 records as of 2026-05-04); cold-cache responses can take 30s+. See `decision-log.md` DL-0003.
> - Stern Vue.js game / bulletin pages — `networkidle` waits routinely take 15–25s.
>
> **Consequence for `FileDownloader` (still has `client.Timeout = 300s`):** the per-attempt cap is now `50s` (was `10s` under the standard-handler default — strictly an improvement). The total-request budget is now `120s` (vs. the `300s` HttpClient outer wall — that wall is now soft-capped). For the file sizes Phase 1 actually downloads (most PDFs < 20 MB), `120s` is comfortably sufficient. If a future Phase surfaces 50 MB+ payloads, the right move is the per-client custom pipeline this research originally recommended (then we get to delete the global override). Until that surfaces, the global bump is the simplest fix that keeps both classes happy.
>
> The original recommendation below is preserved unchanged as the future-state direction; come back to it when per-client resilience tuning becomes necessary.

## TL;DR

**Use `Microsoft.Extensions.Http.Resilience` v10.4.0 with a custom pipeline via `AddResilienceHandler(...)`** (not `AddStandardResilienceHandler` — its defaults are tuned for enterprise outbound calls and are wrong for a polite single-host scraper). Configure two strategies only: a per-host concurrency limiter (1–2) and a Polly retry strategy with exponential backoff + jitter and a `Retry-After`-aware delay. Skip circuit breaker, total-request timeout, and hedging — they are dead weight or actively harmful for this workload. Delete `IsRetryableStatus` / `IsRetryableException` / `ComputeDelay` / `TryGetRetryAfterMs` from `FileDownloader` after migration; the package replaces them.

## A. The landscape

### Microsoft's current recommendation (.NET 8 / 9 / 10 / 11)

The Microsoft Learn page **["Build resilient HTTP apps: Key development patterns"](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience)** (last updated **2026-03-30**, `ms.date: 2026-02-24`) opens with:

> "To help build resilient HTTP apps, the Microsoft.Extensions.Http.Resilience NuGet package provides resilience mechanisms specifically for the HttpClient. This NuGet package relies on the Microsoft.Extensions.Resilience library and Polly, which is a popular open-source project."

The companion landing page **["Introduction to resilient app development"](https://learn.microsoft.com/en-us/dotnet/core/resilience/)** (updated 2026-01-20) lists exactly two recommended packages — `Microsoft.Extensions.Resilience` and `Microsoft.Extensions.Http.Resilience` — and includes a hard deprecation notice for the v7 path:

> **Important**: "The Microsoft.Extensions.Http.Polly NuGet package is deprecated. Use either of the aforementioned packages instead."

Translation: the old "wire up `Polly.Extensions.Http` and `AddPolicyHandler`" pattern from every blog post written between 2018 and 2023 is no longer the supported path. Microsoft's pivot to the v8 `ResiliencePipeline` API and the `Microsoft.Extensions.Http.Resilience` wrapper around it is now several years old and stable.

### Package status (verified against Microsoft Learn API reference, 2026-03-13)

| Package | Latest version | Target frameworks | Status |
|---|---|---|---|
| `Microsoft.Extensions.Http.Resilience` | **10.4.0** | netstandard2.0, net462+, net8.0, net9.0, net10.0, net11.0 | Stable, ships with Microsoft.Extensions release train |
| `Microsoft.Extensions.Resilience` | 10.x | same | Stable |
| `Microsoft.Extensions.Http.Polly` | (frozen) | n/a | **Deprecated by Microsoft** |
| `Polly` (a.k.a. Polly.Core) | v8.x | netstandard2.0+, net6.0+ | GA since November 2023; v8 is the current API |

Both Microsoft API reference pages I cross-checked — [`HttpStandardResilienceOptions`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.http.resilience.httpstandardresilienceoptions) and [`HttpRetryStrategyOptions`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.http.resilience.httpretrystrategyoptions) — display package version `v10.4.0` and a moniker list including `net-10.0-pp` and `net-11.0-pp`, so the package is current and forward-compatible.

### Polly v8 vs v7 (one-paragraph context)

Polly v7's `Policy` / `IAsyncPolicy<T>` / `PolicyWrap` API still works but is in maintenance. Polly v8 introduced `ResiliencePipeline` / `ResiliencePipelineBuilder<T>` with strongly-typed strategy options classes (`RetryStrategyOptions<T>`, `CircuitBreakerStrategyOptions<T>`, `TimeoutStrategyOptions`, etc.), built-in telemetry (Polly emits `Activity` and structured logs against the `Polly` `ActivitySource`), and zero-allocation execution paths. `Microsoft.Extensions.Http.Resilience` is a thin wrapper around v8: `HttpRetryStrategyOptions` literally `: Polly.Retry.RetryStrategyOptions<HttpResponseMessage>` per the [API reference page source link](https://github.com/dotnet/extensions/blob/0ae4c336dd9f1b59b1e25bcf341da7315d657557/src/Libraries/Microsoft.Extensions.Http.Resilience/Polly/HttpRetryStrategyOptions.cs).

### .NET 10 — anything new?

**No new HTTP resilience features in .NET 10.** I read the entire [What's new in .NET libraries for .NET 10](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries) (updated 2026-03-30, `ms.date: 2025-11-07`) — the `Resilience`, `Polly`, and `Http` sections are absent; the only HTTP-area change is client-side TLS 1.3 on macOS via Network.framework. The recommendation stack is unchanged from .NET 8 GA.

## B. Option comparison

### B.1 Microsoft.Extensions.Http.Resilience

**Setup (custom pipeline — what we'd actually use):**

```csharp
// Program.cs
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Retry;

builder.Services.AddHttpClient<FileDownloader>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.UserAgent);
        client.Timeout = TimeSpan.FromSeconds(300);
    })
    .AddResilienceHandler("stern-download", pipeline =>
    {
        pipeline.AddConcurrencyLimiter(permitLimit: 2);
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(30),
            ShouldRetryAfterHeader = true,
            // ShouldHandle defaults already cover 5xx, 408, 429,
            // HttpRequestException, TimeoutRejectedException.
        });
    });

builder.Services.AddHttpClient<ManualsScraper>(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(Constants.UserAgent);
        client.Timeout = TimeSpan.FromSeconds(120);
    })
    .AddResilienceHandler("stern-html", pipeline =>
    {
        pipeline.AddConcurrencyLimiter(permitLimit: 2);
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(30),
            ShouldRetryAfterHeader = true,
        });
    });
```

**Per-client customization:** Trivial — each `AddHttpClient<T>` chain returns its own `IHttpClientBuilder`, and `AddResilienceHandler(name, configure)` accepts a per-client name and per-client delegate. Different clients can have completely different pipelines. You can also call `services.ConfigureHttpClientDefaults(b => b.AddStandardResilienceHandler())` to apply a baseline globally then override per client (Microsoft demonstrates this in the docs with a `RemoveAllResilienceHandlers()` example).

**`AddStandardResilienceHandler` defaults (verbatim from the [Microsoft docs table](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience#standard-resilience-handler-defaults)):**

| # | Strategy | Defaults |
|---|---|---|
| 1 | Rate limiter | Queue `0`, Permit `1_000` |
| 2 | Total timeout | 30s |
| 3 | Retry | Max retries `3`, Exponential backoff, jitter on, base delay 2s |
| 4 | Circuit breaker | Failure ratio 10%, Min throughput `100`, Sampling 30s, Break 5s |
| 5 | Attempt timeout | 10s |

Retry/CB target: HTTP 5xx, 408, 429, plus `HttpRequestException` and `TimeoutRejectedException`.

**Why we can't use these defaults verbatim:** the 30-second total request timeout will guillotine large-PDF downloads (Stern's bigger manuals are ~50 MB and take longer than 30s on a slow link), the 10s per-attempt timeout will guillotine *anything* mid-download, the rate limiter permits 1,000 concurrent requests against one polite host (we want 1–2), and the circuit breaker minimum throughput of 100 means it never trips on a 600-file run before the run ends anyway. Standard defaults are calibrated for service-mesh outbound RPC, not for "be a courteous web scraper."

**Telemetry:** First-class. The package emits via Polly's built-in telemetry (`ResilienceTelemetrySource`), which produces structured `ILogger` events and an `Activity` per execution with tags like `error.type`, `request.name`, `attempt.number`. Microsoft also ships `services.AddResilienceEnricher()` which adds `IExceptionSummarizer`-based exception enrichment and `RequestMetadata` enrichment. Free with the package; you do not have to write any logging code.

**Testability:** Two patterns:
1. *Inject a fake `HttpMessageHandler`*: `AddHttpClient<T>(...).ConfigurePrimaryHttpMessageHandler(() => new FakeHandler())` — Microsoft's documented `IHttpClientFactory` testing approach. The resilience handlers run on top of the fake, so retries are exercised end-to-end. This is the right approach for our use case.
2. *Test the pipeline standalone*: build a `ResiliencePipeline<HttpResponseMessage>` directly with `new ResiliencePipelineBuilder<HttpResponseMessage>().AddRetry(...).Build()` and execute against a stub delegate. Useful for testing custom `ShouldHandle` logic in isolation.

**Dependency footprint:** Pulls in `Microsoft.Extensions.Resilience`, `Microsoft.Extensions.Http`, `Microsoft.Extensions.Diagnostics.ExceptionSummarization`, `Polly.Core`, `Polly.RateLimiting`, `Polly.Extensions`. All Microsoft-published or Microsoft-blessed. Roughly 7 transitive packages totaling ~600 KB. For a hobby project that already pulls Microsoft.Extensions.Hosting 10.0.4 (which itself carries 30+ packages), this is a rounding error.

**Lock-in / migration cost:** Low. The actual policy code uses Polly v8 types (`RetryStrategyOptions`, `DelayBackoffType`, `ResiliencePipelineBuilder`). If we ever drop the wrapper, we'd keep all of that and only swap `AddResilienceHandler(...)` for an `AddHttpMessageHandler<T>(...)` registering a hand-rolled `DelegatingHandler` that calls `pipeline.ExecuteAsync`. ~20 LOC of glue. Going the other direction (away from Polly entirely) is the bigger move, and it's the same move from any of the three options.

### B.2 Polly v8 directly (without the Microsoft wrapper)

**Setup:**

```csharp
// Program.cs
using Polly;
using Polly.Registry;
using Polly.Retry;

builder.Services.AddResiliencePipeline<string, HttpResponseMessage>("stern-http", pipeline =>
{
    pipeline.AddConcurrencyLimiter(permitLimit: 2);
    pipeline.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
    {
        MaxRetryAttempts = 3,
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        Delay = TimeSpan.FromSeconds(1),
        MaxDelay = TimeSpan.FromSeconds(30),
        ShouldHandle = static args =>
        {
            var ex = args.Outcome.Exception;
            var resp = args.Outcome.Result;
            var retryable =
                ex is HttpRequestException or TaskCanceledException ||
                (resp is not null && (
                    (int)resp.StatusCode >= 500 ||
                    resp.StatusCode == HttpStatusCode.RequestTimeout ||
                    (int)resp.StatusCode == 429));
            return ValueTask.FromResult(retryable);
        },
        DelayGenerator = static args =>
        {
            // Honor Retry-After when present.
            if (args.Outcome.Result is { } r && r.Headers.RetryAfter is { } ra)
            {
                if (ra.Delta is { } d) return ValueTask.FromResult<TimeSpan?>(d);
                if (ra.Date is { } abs) return ValueTask.FromResult<TimeSpan?>(abs - DateTimeOffset.UtcNow);
            }
            return ValueTask.FromResult<TimeSpan?>(null); // fall back to BackoffType
        }
    });
});

// Custom DelegatingHandler bridging the pipeline to HttpClient
public sealed class PollyHandler(ResiliencePipelineProvider<string> provider) : DelegatingHandler
{
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline =
        provider.GetPipeline<HttpResponseMessage>("stern-http");

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct) =>
        _pipeline.ExecuteAsync(
            async token => await base.SendAsync(request, token), ct).AsTask();
}

// And in registration:
builder.Services.AddTransient<PollyHandler>();
builder.Services.AddHttpClient<FileDownloader>(...)
    .AddHttpMessageHandler<PollyHandler>();
```

**Per-client customization:** Possible by registering multiple named pipelines and multiple `DelegatingHandler` types (or a single parameterized one). More boilerplate than B.1.

**Standard policy defaults:** N/A — there is no equivalent of `AddStandardResilienceHandler`. You build the pipeline entirely.

**Telemetry:** Same Polly v8 telemetry as B.1; no `ILogger` integration is automatic until you wire `Microsoft.Extensions.Logging` into Polly's `TelemetryListener`. The Microsoft wrapper does this for you; here you'd do it yourself.

**Testability:** Same patterns as B.1 — pipelines are independently constructable, and `ConfigurePrimaryHttpMessageHandler` for end-to-end tests.

**Dependency footprint:** Smaller — just `Polly.Core` and (for the rate limiter) `Polly.RateLimiting`. ~3 transitive packages.

**Lock-in / migration cost:** Lowest in absolute terms (you wrote the glue), but the *delta* from B.1 is small because B.1 *is* this with the glue pre-written. Migrating from B.2 to B.1 later is essentially deleting your `PollyHandler` and switching the registration extension method.

### B.3 Hand-rolled `DelegatingHandler`

**Setup:**

```csharp
public sealed class RetryHandler : DelegatingHandler
{
    private static readonly HttpStatusCode[] RetryStatuses =
        [HttpStatusCode.RequestTimeout, (HttpStatusCode)429,
         HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway,
         HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout];

    private readonly ILogger<RetryHandler> _logger;
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;

    public RetryHandler(ILogger<RetryHandler> logger, IOptions<ScraperSettings> options)
    {
        _logger = logger;
        _maxRetries = options.Value.MaxRetries;
        _baseDelay = TimeSpan.FromMilliseconds(options.Value.InitialRetryDelayMs);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage? resp = null;
            try
            {
                resp = await base.SendAsync(request, ct);
                if (resp.IsSuccessStatusCode || !RetryStatuses.Contains(resp.StatusCode))
                    return resp;
                if (attempt >= _maxRetries) return resp;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                        && !ct.IsCancellationRequested
                                        && attempt < _maxRetries)
            {
                _logger.LogWarning(ex, "Transient on attempt {N}", attempt + 1);
            }

            var delay = ComputeDelay(attempt, resp);
            resp?.Dispose();
            await Task.Delay(delay, ct);
        }
    }

    private TimeSpan ComputeDelay(int attempt, HttpResponseMessage? resp) =>
        // ... (your existing math, plus jitter)
        TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * Math.Pow(2, attempt));
}

// Registration
builder.Services.AddTransient<RetryHandler>();
builder.Services.AddHttpClient<FileDownloader>(...).AddHttpMessageHandler<RetryHandler>();
builder.Services.AddHttpClient<ManualsScraper>(...).AddHttpMessageHandler<RetryHandler>();
```

**Per-client customization:** Per-client config requires either constructor params per client (DI cannot easily inject "different settings per typed-client") or multiple handler classes. Awkward.

**Standard policy defaults:** N/A.

**Telemetry:** Whatever you write. No automatic `Activity`, no enrichment, no metering.

**Testability:** A `DelegatingHandler` can be tested with a fake inner handler. Straightforward but you write everything.

**Dependency footprint:** Zero new packages. Smallest of the three.

**Lock-in / migration cost:** Zero out, high in: if you later want hedging, circuit breaker, rate limiting, or anything else, you re-implement each one badly. You also can't easily share telemetry conventions with other Polly-using projects.

## C. Recommended configuration for PinballWizard

The standard handler is wrong by default. We need a *trimmed* pipeline. Stage-by-stage rationale:

### Concurrency limiter — **YES, permit 1–2 per host**

CLAUDE.md principle: "be polite to sternpinball.com, don't re-download unchanged files." A single-host hobby scraper hammering a small business site with 600 parallel requests is bad citizenship even if the host doesn't enforce it. `AddConcurrencyLimiter(2)` caps in-flight to 2; combined with `Task.WhenAll` in `ScraperOrchestrator`, this self-throttles regardless of caller-side parallelism.

Note: this is a *concurrency* limiter, not a throughput rate limiter. If we later need "no more than N requests per second" we'd add `AddRateLimiter` with a `SlidingWindowRateLimiter`. For now, concurrency=2 is sufficient politeness.

### Retry — **YES, 3 attempts, exponential backoff, jitter, Retry-After-aware**

Configuration:
```csharp
new HttpRetryStrategyOptions
{
    MaxRetryAttempts   = 3,
    BackoffType        = DelayBackoffType.Exponential,
    UseJitter          = true,                 // avoid thundering herd
    Delay              = TimeSpan.FromSeconds(1),
    MaxDelay           = TimeSpan.FromSeconds(30),
    ShouldRetryAfterHeader = true,             // honor 429/503 Retry-After
}
```

Rationale: identical retry semantics to today's `FileDownloader` (5xx + 408 + 429 + transport exceptions, exponential with cap), plus jitter (which today's hand-rolled version lacks) and a `MaxDelay` cap. Jitter is free and removes a real-world failure mode; the bare exponential we have today will lockstep retries when multiple workers fail at once. `ShouldRetryAfterHeader = true` makes the strategy honor `Retry-After` automatically — it replaces the entire `TryGetRetryAfterMs` helper.

The default `ShouldHandle` predicate already covers our criteria (5xx, 408, 429, `HttpRequestException`, `TimeoutRejectedException`) — verified against [`HttpClientResiliencePredicates`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.http.resilience.httpclientresiliencepredicates) in the package's namespace. We do not need to override `ShouldHandle`.

### Per-attempt timeout — **NO** (use `HttpClient.Timeout` instead)

The standard handler's 10s per-attempt timeout is fatal for 50 MB PDF downloads. We already set `HttpClient.Timeout = 300s` on `FileDownloader` and `120s` on `ManualsScraper`. That's sufficient and applies per-attempt naturally (each retry gets its own 300s budget). Adding an explicit `AddTimeout` on top is redundant noise.

### Total request timeout — **NO**

Same reason. Total of 30s would fail on any large download. We don't have an SLA forcing us to bound total wall-clock. Cancellation tokens propagate from the orchestrator already.

### Circuit breaker — **NO**

Circuit breakers protect *callers* from a sustained-down dependency by failing fast. The standard CB needs 100 throughput in 30s before it considers tripping — we won't hit that with concurrency=2 and ~600 files spread over a single overnight run. It'd never engage. If Stern goes down mid-run, retries will exhaust per-file and we'll log failures; the orchestrator continues with the next file. That is the correct behavior for a batch crawler (we want partial success, not "abort the whole run"). The circuit breaker only buys value if you have many requests per second to a single host. We don't.

### Rate limiter (throughput) — **NO** (concurrency limiter is enough)

Per above — concurrency=2 covers politeness without needing windowed rate accounting.

### Hedging — **NO, definitively**

Hedging fires duplicate requests in parallel hoping one returns faster. For a scraper hitting a single polite host, this *doubles* load and produces no value (no alternate endpoints exist). It's an enterprise pattern for redundant backends.

### What this gets us vs today's `FileDownloader.DownloadAsync`

- ✅ Retries on the same status codes / exceptions
- ✅ Exponential backoff with cap
- ✅ Honors `Retry-After`
- ➕ Adds jitter (today: missing)
- ➕ Adds `MaxDelay` cap separate from `MaxBackoffMs` constant (today: hardcoded 30s)
- ➕ Centralizes retry policy in one place (today: only `FileDownloader` retries; `ManualsScraper.GetStringAsync` does not — *the gap that motivated this research*)
- ➕ Polly emits telemetry (`Activity`, `ILogger`, metrics) for free
- ⚠️ **Stream resumption is not handled by either the current code or any of the three options.** All three retry the *full* request from scratch. None of them parse the partially-written file, send a `Range:` header, and resume. If we want resumable downloads, that's a separate `FileDownloader`-level feature using HTTP range requests — not something the resilience handler can do, because by the time the response body is mid-stream, the handler has already returned. **For phase 1 this is fine**: the `MaxDelay = 30s` cap and Stern's small PDFs (most under 20 MB) mean a from-scratch retry on stream failure costs at most ~30s of redundant transfer. Document but defer. (See "Stream resumption" note below.)

## D. Decision

**Adopt `Microsoft.Extensions.Http.Resilience` v10.4.0 and use `AddResilienceHandler` (custom pipeline) — not `AddStandardResilienceHandler` — with the configuration in section C.** Microsoft's official .NET 10-era recommendation is unambiguous, the package wraps Polly v8 (which the ecosystem has converged on) so we get telemetry and testing patterns aligned with the wider .NET community, and the per-client `AddResilienceHandler` API gives us trivially different pipelines for `FileDownloader`, `ManualsScraper`, and the future Phase 2 Azure AI clients without code duplication. The dependency cost is negligible against a project that already pulls `Microsoft.Extensions.Hosting`.

**Strongest counter-argument (B.3 hand-rolled):** "We already have working retry math; one more class avoids any new dependencies and the math is ~50 lines." That's true and tempting for a hobby project. It loses because (a) we'd still need to add it to `ManualsScraper` (the documented gap) — duplicating logic across two consumers is exactly the cross-cutting smell the resilience pipeline pattern solves; (b) we lose Polly's telemetry, which we'll regret the first time we want to know why a run failed at 3 a.m.; (c) Phase 2's API service will need the *same* policy against Azure endpoints and the standard package config will compose in seconds there, whereas a hand-rolled handler would ship with Phase 1's bespoke math attached forever; (d) the "no dependencies" argument is hollow given we already depend on `Microsoft.Extensions.Hosting 10.0.4`, and `Microsoft.Extensions.Http.Resilience` ships from the same release train at the same cadence.

B.2 (Polly direct) loses to B.1 because the Microsoft wrapper *is* B.2 with ~30 lines of glue pre-written, plus the `ConfigureHttpClientDefaults` integration, plus `HttpRetryStrategyOptions.ShouldRetryAfterHeader`, plus the Microsoft enrichment hooks. There's no reason to skip it.

## Migration path

Files changed:

1. **`PinballWizard.Scraper.csproj`** — add one `PackageReference`:
   ```xml
   <PackageReference Include="Microsoft.Extensions.Http.Resilience" Version="10.4.0" />
   ```

2. **`Program.cs` (lines 168–183)** — replace the two `AddHttpClient<T>` blocks with the version in section B.1. Optionally extract the pipeline-config delegate to a private static method `ConfigureSternPipeline(ResiliencePipelineBuilder<HttpResponseMessage>)` to share between the two clients.

3. **`Downloading/FileDownloader.cs`** — delete the entire retry loop:
   - The outer `for (var attempt = 0; ...)` becomes a single straight-line execution.
   - Delete `MaxBackoffMs`, `MaxRetryAfterSeconds` constants.
   - Delete `IsRetryableStatus`, `IsRetryableException`, `ComputeDelay`, `TryGetRetryAfterMs` helpers.
   - Delete the `catch (Exception ex) when (IsRetryableException(ex) ...)` clause.
   - Keep the `catch (OperationCanceledException ...) when (...)` for caller-cancellation pass-through.
   - Keep the final `catch` that converts non-retryable failures into a `Failed` `DownloadResult` — Polly will surface terminal failures as the original exception, which we still want to convert into a domain result rather than throw.
   - Rough size: −80 LOC, file shrinks from 326 lines to ~245.

4. **`Infrastructure/ScraperSettings.cs`** — `MaxRetries` and `InitialRetryDelayMs` become unused. Either:
   - Delete them (cleanest), and remove from `appsettings.json`, OR
   - Keep them and bind them into the resilience pipeline config in `Program.cs` for runtime configurability (recommended — preserves the operator's ability to tune via JSON without rebuild).

5. **Tests** — any unit tests that assert on retry behavior in `FileDownloader` should be moved to either:
   - A new test class that asserts on the registered pipeline behavior end-to-end via `ConfigurePrimaryHttpMessageHandler` with a fake handler, OR
   - Be deleted if they were only verifying the hand-rolled math (the math is now Polly's responsibility and Polly has its own test suite).

**Total LOC delta:** ~−60 net (delete ~80 from FileDownloader, add ~20 in Program.cs).

**Stream resumption (open issue, not blocking):** Neither today's code nor any of the three options resumes a partially-downloaded file. If a `ReadAsStreamAsync` enumeration fails midway through a 50 MB PDF, we re-download from byte 0. To fix, `FileDownloader` would need to: (a) detect partial-write failures separately from initial-request failures, (b) on retry, send `Range: bytes=N-` where N is the byte count successfully written, (c) verify the server responds 206 Partial Content (not all servers do — Stern's CDN may or may not), (d) append rather than overwrite. This is independent of the resilience handler choice. Recommend filing as a separate follow-up; for the file sizes we're dealing with (mostly < 20 MB), a from-scratch retry is acceptable.

---

### Sources cited

1. [Build resilient HTTP apps: Key development patterns — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience) (updated 2026-03-30)
2. [Introduction to resilient app development — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/resilience/) (updated 2026-01-20)
3. [HttpStandardResilienceOptions Class — Microsoft Learn API ref](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.http.resilience.httpstandardresilienceoptions) (package v10.4.0, generated 2026-03-13)
4. [HttpRetryStrategyOptions Class — Microsoft Learn API ref](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.http.resilience.httpretrystrategyoptions) (package v10.4.0, generated 2026-03-13)
5. [Microsoft.Extensions.Http.Resilience Namespace — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.http.resilience) (full type listing)
6. [ResilienceHttpClientBuilderExtensions.AddResilienceHandler Method — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.dependencyinjection.resiliencehttpclientbuilderextensions.addresiliencehandler) (overload signatures, package v10.4.0)
7. [Use the IHttpClientFactory — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-factory) (updated 2026-03-30)
8. [What's new in .NET libraries for .NET 10 — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries) (no new resilience features in .NET 10 — verified by absence)

### Sources I tried to fetch but could not

- `nuget.org` — domain blocked in this environment. Package version 10.4.0 was instead verified via Microsoft Learn API reference page metadata, which displays the source assembly version.
- `pollydocs.org` — domain blocked. Polly v8 surface area was instead verified via the `Microsoft.Extensions.Http.Resilience` API reference pages, which expose the underlying Polly types (e.g., `HttpRetryStrategyOptions : Polly.Retry.RetryStrategyOptions<HttpResponseMessage>`) and link to their source on GitHub.
- `github.com/App-vNext/Polly` README — blocked. Polly v8 GA status was inferred from Microsoft's deprecation notice on `Microsoft.Extensions.Http.Polly` (which only makes sense if v8 is the supported path) and from the .NET 10 / .NET 11 monikers being present on every Polly-related Microsoft API page.

If anything in section A or B should be double-checked, the underlined items above are where to focus. The TL;DR and the stage-by-stage rationale in section C do not depend on those blocked sources.
