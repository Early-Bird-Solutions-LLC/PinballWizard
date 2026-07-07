# Machine-Scope Zero-Content Gate — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Skip the Foundry agent turn on a machine-scoped "Ask the Wizard" ask when the RAG index holds zero chunks for that machine, returning the identical deterministic community-resource refusal instead.

**Architecture:** A new Application port `IMachineCorpusCoverage` (implemented in Infrastructure over the same AI Search `SearchClient` and `BuildFilter` the retriever uses) answers "does this machine have any indexed chunks?" via a `Size=0, IncludeTotalCount=true` count query. `AiRouter.AnswerStreamingAsync` gains an optional `machineId`; after a semantic-cache miss and before the agent call, if `machineId` is supplied and coverage is zero, it emits the same `NoCitation` recovery the post-agent guardrail would have produced — no LLM call. The detail-page "Ask the Wizard" button threads `_machine.Id` through `/wizard?machineId=` → the ask-stream request → the router.

**Tech Stack:** .NET 10, Clean Architecture (Core/Application/Infrastructure/Api/Web), Azure.Search.Documents `SearchClient`, Microsoft.Agents.AI (Foundry), Blazor Web App + SSE, xUnit + NSubstitute, `System.Diagnostics.Metrics` (OTel).

## Global Constraints

- **Personal identity only.** Every commit authors as `Jim Keeley <94459922+jkeeley2073@users.noreply.github.com>`; **no Claude attribution trailer**.
- **Never on `main`.** All work happens on branch `feat/machine-scope-no-content-gate` in the worktree `.worktrees/feat-machine-scope-no-content-gate` (already created).
- **No masking fallbacks (invariant #17).** A coverage-check failure must NOT silently gate NOR be swallowed — meter it and fall through to the full agent path.
- **No XML doc comments** on public surface (repo convention). Use regular `//` comments for rationale where it earns its place.
- **Tests assert behavior, not structure.** The gate-fires test proves the agent was never invoked; the gate-does-not-fire test proves a metadata-card-only machine still reaches the agent.
- **Clean Architecture layering.** The port lives in Application; the AI Search implementation lives in Infrastructure. Application takes no Infrastructure reference.
- **Filter parity is the safety invariant.** The coverage count filter is built from the *same* `AiSearchRagRetriever.BuildFilter` the retrieval path uses; a contract test locks this.
- **CI-equivalent test command:** `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"` (run before push).
- **All paths below are relative to the worktree root** `.worktrees/feat-machine-scope-no-content-gate/`.

---

### Task 1: `IMachineCorpusCoverage` port + AI Search implementation + filter-parity test + DI

**Files:**
- Create: `src/PinballWizard.Application/Ai/Retrieval/IMachineCorpusCoverage.cs`
- Create: `src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchMachineCorpusCoverage.cs`
- Create: `tests/PinballWizard.Infrastructure.Tests/Rag/Retrieval/AiSearchMachineCorpusCoverageTests.cs`
- Modify: `src/PinballWizard.Infrastructure/Integrations/AiSearch/ServiceCollectionExtensions.cs` (register the port next to `IRagRetriever` at line 102)

**Interfaces:**
- Produces: `IMachineCorpusCoverage.HasIndexedContentAsync(string machineId, CancellationToken ct) : Task<bool>` — `true` when ≥1 indexed chunk exists for the machine.
- Consumes: `AiSearchRagRetriever.BuildFilter(RetrievalOptions)` (internal static, visible to `PinballWizard.Infrastructure.Tests` via `InternalsVisibleTo`), `RetrievalOptions` (Application), `AiSearchIndexFields.MachineId` (Infrastructure internal), `AiSearchOptions`, `RetrievedChunkDocument`.

- [ ] **Step 1: Write the Application port**

Create `src/PinballWizard.Application/Ai/Retrieval/IMachineCorpusCoverage.cs`:

```csharp
namespace PinballWizard.Application.Ai.Retrieval;

// Answers "does the RAG index hold any chunks for this machine?" without
// running retrieval or the LLM. Used by AiRouter (ADR-0052) to skip the
// Foundry agent turn on a machine-scoped ask that could only ever refuse.
// Backed by the SAME AI Search index and machine_id filter the retriever
// uses, so a positive answer means the agent genuinely has grounding
// (e.g. a synthesized metadata card) and must run.
public interface IMachineCorpusCoverage
{
    Task<bool> HasIndexedContentAsync(string machineId, CancellationToken ct);
}
```

- [ ] **Step 2: Write the failing filter-parity + behavior test**

Create `tests/PinballWizard.Infrastructure.Tests/Rag/Retrieval/AiSearchMachineCorpusCoverageTests.cs`:

```csharp
using PinballWizard.Application.Ai.Retrieval;
using PinballWizard.Infrastructure.Rag.Retrieval;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Rag.Retrieval;

public sealed class AiSearchMachineCorpusCoverageTests
{
    // Safety invariant (ADR-0052): the coverage count filter MUST be
    // byte-identical to the retriever's machine-scoped filter, so a
    // "zero content" verdict can never disagree with what the agent's
    // own machine-scoped search would see. Both derive from BuildFilter.
    [Fact]
    public void CountFilter_IsIdenticalTo_RetrieverMachineFilter()
    {
        const string machineId = "GRBN-MQR4P";

        var coverageFilter = AiSearchMachineCorpusCoverage.BuildCountFilter(machineId);
        var retrieverFilter = AiSearchRagRetriever.BuildFilter(
            new RetrievalOptions(MachineId: machineId));

        Assert.Equal(retrieverFilter, coverageFilter);
        Assert.Equal("machine_id eq 'GRBN-MQR4P'", coverageFilter);
    }

    // OData escaping must also be identical for ids containing an
    // apostrophe (a fan-named machine), or the two filters could diverge
    // on exactly the untrusted-input case escaping exists to handle.
    [Fact]
    public void CountFilter_EscapesApostrophe_IdenticalToRetriever()
    {
        const string machineId = "O'Brien-1";

        var coverageFilter = AiSearchMachineCorpusCoverage.BuildCountFilter(machineId);
        var retrieverFilter = AiSearchRagRetriever.BuildFilter(
            new RetrievalOptions(MachineId: machineId));

        Assert.Equal(retrieverFilter, coverageFilter);
        Assert.Equal("machine_id eq 'O''Brien-1'", coverageFilter);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~AiSearchMachineCorpusCoverageTests"`
Expected: FAIL — `AiSearchMachineCorpusCoverage` does not exist (compile error).

- [ ] **Step 4: Write the Infrastructure implementation**

Create `src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchMachineCorpusCoverage.cs`:

```csharp
using Azure.Search.Documents;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Ai.Retrieval;

namespace PinballWizard.Infrastructure.Rag.Retrieval;

// AI Search implementation of IMachineCorpusCoverage (ADR-0052). Issues a
// Size=0, IncludeTotalCount=true count over the corpus index scoped to
// machine_id — the same pattern CosmosAiSearchRagReconciler.CountChunksAsync
// uses — and reuses AiSearchRagRetriever.BuildFilter so the machine filter
// is provably identical to the retrieval path (see the parity contract test).
public sealed class AiSearchMachineCorpusCoverage : IMachineCorpusCoverage
{
    private readonly SearchClient _searchClient;
    private readonly ILogger<AiSearchMachineCorpusCoverage> _logger;

    public AiSearchMachineCorpusCoverage(
        SearchClient searchClient,
        ILogger<AiSearchMachineCorpusCoverage> logger)
    {
        ArgumentNullException.ThrowIfNull(searchClient);
        ArgumentNullException.ThrowIfNull(logger);
        _searchClient = searchClient;
        _logger = logger;
    }

    public async Task<bool> HasIndexedContentAsync(string machineId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);

        var options = new SearchOptions
        {
            Filter = BuildCountFilter(machineId),
            IncludeTotalCount = true,
            Size = 0,
        };

        var response = await _searchClient
            .SearchAsync<RetrievedChunkDocument>(searchText: "*", options, ct)
            .ConfigureAwait(false);

        var count = response.Value.TotalCount ?? 0;
        return count > 0;
    }

    // Single-clause machine filter, delegated to the retriever's builder so
    // the coverage query and real retrieval can never diverge on filter
    // shape or OData escaping. Non-null for any non-empty machineId.
    internal static string BuildCountFilter(string machineId)
        => AiSearchRagRetriever.BuildFilter(new RetrievalOptions(MachineId: machineId))!;
}
```

- [ ] **Step 5: Run the parity test to verify it passes**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~AiSearchMachineCorpusCoverageTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Register the port in DI**

In `src/PinballWizard.Infrastructure/Integrations/AiSearch/ServiceCollectionExtensions.cs`, immediately after the existing `services.TryAddSingleton<IRagRetriever>(BuildRagRetriever);` (line 102), add:

```csharp
        services.TryAddSingleton<IMachineCorpusCoverage>(BuildMachineCorpusCoverage);
```

Then add this factory method next to `BuildRagRetriever` (near line 200), mirroring how it builds its `SearchClient`:

```csharp
    private static AiSearchMachineCorpusCoverage BuildMachineCorpusCoverage(IServiceProvider sp)
    {
        var aiSearchOptions = sp.GetRequiredService<IOptions<AiSearchOptions>>().Value;
        var searchClient = new SearchClient(
            new Uri(aiSearchOptions.Endpoint),
            aiSearchOptions.IndexName,
            Credentials.SharedAzureCredential.Instance);

        return new AiSearchMachineCorpusCoverage(
            searchClient,
            sp.GetRequiredService<ILogger<AiSearchMachineCorpusCoverage>>());
    }
```

Add `using PinballWizard.Application.Ai.Retrieval;` and `using PinballWizard.Infrastructure.Rag.Retrieval;` to the file's usings if not already present.

- [ ] **Step 7: Build to verify DI wiring compiles**

Run: `dotnet build src/PinballWizard.Infrastructure`
Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
cd .worktrees/feat-machine-scope-no-content-gate
git add src/PinballWizard.Application/Ai/Retrieval/IMachineCorpusCoverage.cs \
        src/PinballWizard.Infrastructure/Rag/Retrieval/AiSearchMachineCorpusCoverage.cs \
        tests/PinballWizard.Infrastructure.Tests/Rag/Retrieval/AiSearchMachineCorpusCoverageTests.cs \
        src/PinballWizard.Infrastructure/Integrations/AiSearch/ServiceCollectionExtensions.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "feat(rag) IMachineCorpusCoverage — machine-scoped chunk-count over AI Search (ADR-0052)"
```

---

### Task 2: Telemetry counters

**Files:**
- Modify: `src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs` (add two counters alongside the existing `AiRefusals` / `AiCacheHits` declarations, ~line 116)

**Interfaces:**
- Produces: `PinballWizardTelemetry.AiMachineScopeGateShortCircuits : Counter<long>`, `PinballWizardTelemetry.AiMachineScopeGateErrors : Counter<long>`.

- [ ] **Step 1: Add the counter declarations**

In `src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs`, after the `AiRefusals` counter declaration (lines 116–119), add:

```csharp
    public static readonly Counter<long> AiMachineScopeGateShortCircuits = Meter.CreateCounter<long>(
        "pinwiz.ai.machine_scope_gate.short_circuits_total",
        unit: "{question}",
        description: "Machine-scoped asks answered by the deterministic zero-content gate (ADR-0052) — the machine had zero indexed chunks, so the community-resource refusal was returned WITHOUT invoking the Foundry agent. The firing rate is the token/latency saving; a rise for a supported manufacturer is a leading indicator of an ingestion gap.");

    public static readonly Counter<long> AiMachineScopeGateErrors = Meter.CreateCounter<long>(
        "pinwiz.ai.machine_scope_gate.errors_total",
        unit: "{failure}",
        description: "Coverage-count lookups that failed while evaluating the ADR-0052 gate. On failure the router does NOT gate and falls through to the full agent path (no masking, invariant #17); this counter makes the skipped-optimization visible rather than silent.");
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/PinballWizard.Application`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/PinballWizard.Application/Observability/PinballWizardTelemetry.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "feat(observability) machine-scope gate counters — short-circuits + errors (ADR-0052)"
```

---

### Task 3: Router — `machineId` parameter + zero-content gate

**Files:**
- Modify: `src/PinballWizard.Application/Ai/IAiRouter.cs` (overloads, lines 29 + 45–49)
- Modify: `src/PinballWizard.Application/Ai/AiRouter.cs` (constructor lines 80–125; single-turn shim lines 301–304; canonical method signature line 306–309; gate insertion between lines 377 and 379)
- Modify: `tests/PinballWizard.Infrastructure.Tests/Ai/AiRouterStreamingTests.cs` (add the router `BuildRouter` a `IMachineCorpusCoverage` dependency + two gate tests)

**Interfaces:**
- Consumes: `IMachineCorpusCoverage.HasIndexedContentAsync` (Task 1); `PinballWizardTelemetry.AiMachineScopeGateShortCircuits` / `AiMachineScopeGateErrors` (Task 2); existing private members `BuildRefusalText(RefusalCategory)`, `BuildRefusalDetail(RefusalCategory, ConfidenceSignals?, RefusalDetail?)`, `RecordFirstTokenMs(Stopwatch, string, string)`, `_degradationContext.Snapshot()`, `_refusalRecovery.BuildRecoveryAsync`.
- Produces: `IAiRouter.AnswerStreamingAsync(string question, IReadOnlyList<ConversationTurn>? history, string? machineId, CancellationToken ct)` — the new canonical 4-arg overload.

- [ ] **Step 1: Extend the `IAiRouter` interface**

In `src/PinballWizard.Application/Ai/IAiRouter.cs`, replace the two existing `AnswerStreamingAsync` declarations (the single-turn at line 29 and the multi-turn default-method at lines 45–49) with three overloads that all funnel into a new 4-arg canonical method:

```csharp
    IAsyncEnumerable<AnswerChunk> AnswerStreamingAsync(
        string question,
        CancellationToken cancellationToken)
        => AnswerStreamingAsync(question, history: null, machineId: null, cancellationToken);

    IAsyncEnumerable<AnswerChunk> AnswerStreamingAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        CancellationToken cancellationToken)
        => AnswerStreamingAsync(question, history, machineId: null, cancellationToken);

    // Canonical overload (ADR-0052). machineId, when non-null, pins the ask
    // to a specific machine so the router can skip the agent turn if the RAG
    // index holds no chunks for it. Null preserves prior free-text behaviour.
    IAsyncEnumerable<AnswerChunk> AnswerStreamingAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        string? machineId,
        CancellationToken cancellationToken);
```

- [ ] **Step 2: Add the coverage dependency to `AiRouter`'s constructor**

In `src/PinballWizard.Application/Ai/AiRouter.cs`, add a field near the other readonly fields:

```csharp
    private readonly IMachineCorpusCoverage _machineCorpusCoverage;
```

Add `IMachineCorpusCoverage machineCorpusCoverage` to the constructor parameter list (after `IRefusalRecoveryService refusalRecovery,`), add its null-guard alongside the other `ArgumentNullException.ThrowIfNull` guards, and assign it:

```csharp
        _machineCorpusCoverage = machineCorpusCoverage;
```

Add `using PinballWizard.Application.Ai.Retrieval;` to the file's usings if not present.

- [ ] **Step 3: Update the single-turn shim and the canonical method signature**

Replace the single-turn shim (lines 301–304) so it funnels through the 4-arg method:

```csharp
    public IAsyncEnumerable<AnswerChunk> AnswerStreamingAsync(
        string question,
        CancellationToken cancellationToken)
        => AnswerStreamingAsync(question, history: null, machineId: null, cancellationToken);
```

Change the canonical method signature (lines 306–309) to add `machineId`:

```csharp
    public async IAsyncEnumerable<AnswerChunk> AnswerStreamingAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        string? machineId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
```

**Keep an explicit 3-arg shim on `AiRouter`** (do NOT rely on the interface
default method — C# default interface methods are not callable through the
concrete `AiRouter` type, and existing tests call the 3-arg overload on a
concrete `AiRouter`). Add, next to the single-turn shim:

```csharp
    public IAsyncEnumerable<AnswerChunk> AnswerStreamingAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        CancellationToken cancellationToken)
        => AnswerStreamingAsync(question, history, machineId: null, cancellationToken);
```

So `AiRouter` ends with three concrete methods: the 1-arg shim, the 3-arg
shim, and the 4-arg canonical body — all reachable on the concrete type.

- [ ] **Step 4: Insert the gate between the cache-miss counters and the agent call**

In the canonical method body, immediately after the cache-miss `if/else` block (the `else { PinballWizardTelemetry.AiCacheBypassMultiturn.Add(1); }` that closes at line 377) and BEFORE `var wizardAgent = _agentFactory.GetAgent(AgentName.Wizard);` (line 379), insert:

```csharp
        // ── Machine-scope zero-content gate (ADR-0052) ────────────────
        // When the caller pins the ask to a specific machine and the RAG
        // index holds no chunks for it, the agent turn can only end in a
        // NoCitation refusal. Reproduce that refusal deterministically —
        // no LLM call — via the same recovery the post-agent guardrail
        // would build. Gate ONLY on chunk count (not the page's doc-link
        // count, which misses synthesized metadata cards/overviews).
        if (!string.IsNullOrEmpty(machineId))
        {
            bool hasIndexedContent;
            try
            {
                hasIndexedContent = await _machineCorpusCoverage
                    .HasIndexedContentAsync(machineId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Degrade visibly (invariant #17): a coverage-check failure
                // must not gate NOR be swallowed. Meter it and fall through
                // to the full agent path, which runs its own grounded search.
                PinballWizardTelemetry.AiMachineScopeGateErrors.Add(1);
                _logger.LogWarning(
                    ex,
                    "AiRouter machine-scope coverage check failed for MachineId={MachineId}; proceeding to the agent without gating.",
                    machineId);
                hasIndexedContent = true;
            }

            if (!hasIndexedContent)
            {
                PinballWizardTelemetry.AiMachineScopeGateShortCircuits.Add(1);
                _logger.LogInformation(
                    "AiRouter machine-scope gate short-circuit: MachineId={MachineId} has zero indexed chunks; returning the deterministic community-resource recovery without invoking the agent.",
                    machineId);

                var gateRecovery = await _refusalRecovery
                    .BuildRecoveryAsync(normalized, RefusalCategory.NoCitation, cancellationToken)
                    .ConfigureAwait(false);

                var gateAnswer = new WizardAnswer(
                    Text: BuildRefusalText(RefusalCategory.NoCitation),
                    Citations: [],
                    SubAgentUsed: AgentName.Wizard,
                    Confidence: 0.0,
                    Escalated: false,
                    IsRefusal: true,
                    RefusalCategory: RefusalCategory.NoCitation,
                    PromptVersion: promptVersion,
                    FoundryThreadId: null,
                    RefusalDetail: BuildRefusalDetail(RefusalCategory.NoCitation, signals: null, recovery: gateRecovery),
                    Degradation: _degradationContext.Snapshot());

                RecordFirstTokenMs(requestStopwatch, cacheState: "miss", outcome: "refusal");
                yield return new AnswerChunk.Refusal(RefusalCategory.NoCitation, gateAnswer.Text);
                yield return new AnswerChunk.Final(gateAnswer);
                yield break;
            }
        }
```

- [ ] **Step 5: Write the failing router gate tests**

In `tests/PinballWizard.Infrastructure.Tests/Ai/AiRouterStreamingTests.cs`, first update the router-construction helper to inject the new dependency. Find the `new AiRouter(...)` construction (lines ~799–812) and add an `IMachineCorpusCoverage` argument after the `refusalRecovery` argument. If the helper does not already accept one, introduce a substitute defaulted to "has content" so existing tests are unaffected:

```csharp
        var machineCorpusCoverage = Substitute.For<IMachineCorpusCoverage>();
        machineCorpusCoverage
            .HasIndexedContentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true); // default: machines have content, so existing tests still hit the agent
```

and pass `machineCorpusCoverage` into the `new AiRouter(...)` call in the correct position (after `refusalRecovery`).

Then add two tests (put them at the end of the class). Add `using PinballWizard.Application.Ai.Retrieval;` to the file:

```csharp
    [Fact]
    public async Task MachineScopedAsk_WithZeroIndexedChunks_ShortCircuits_WithoutInvokingAgent()
    {
        // Arrange: coverage says this machine has NO indexed chunks.
        var agentFactory = Substitute.For<IFoundryAgentFactory>();
        var coverage = Substitute.For<IMachineCorpusCoverage>();
        coverage.HasIndexedContentAsync("EMPTY-MACHINE", Arg.Any<CancellationToken>())
                .Returns(false);

        var router = BuildRouterWithCoverage(agentFactory, coverage);

        // Act
        var chunks = await CollectAsync(router.AnswerStreamingAsync(
            "tell me about Super Flipp", history: null, machineId: "EMPTY-MACHINE", CancellationToken.None));

        // Assert: agent never resolved, and a NoCitation refusal came back.
        agentFactory.DidNotReceive().GetAgent(Arg.Any<string>());

        var final = Assert.IsType<AnswerChunk.Final>(chunks[^1]);
        Assert.True(final.Answer.IsRefusal);
        Assert.Equal(RefusalCategory.NoCitation, final.Answer.RefusalCategory);
    }

    [Fact]
    public async Task MachineScopedAsk_WithIndexedChunks_InvokesAgent_DoesNotShortCircuit()
    {
        // Arrange: coverage says this machine HAS at least one chunk
        // (e.g. a synthesized metadata card) — the agent must run.
        var agentUpdates = BuildGroundedAgentUpdates(); // existing helper producing a citation-bearing answer
        var fakeAgent = new FakeStreamingAgent(agentUpdates.ToList());
        var agentFactory = Substitute.For<IFoundryAgentFactory>();
        agentFactory.GetAgent(Arg.Any<string>()).Returns(fakeAgent);

        var coverage = Substitute.For<IMachineCorpusCoverage>();
        coverage.HasIndexedContentAsync("HAS-CARD", Arg.Any<CancellationToken>())
                .Returns(true);

        var router = BuildRouterWithCoverage(agentFactory, coverage);

        // Act
        _ = await CollectAsync(router.AnswerStreamingAsync(
            "tell me about Godzilla", history: null, machineId: "HAS-CARD", CancellationToken.None));

        // Assert: the agent WAS resolved — a metadata-card-only machine is
        // never suppressed by the gate.
        agentFactory.Received().GetAgent(Arg.Any<string>());
    }
```

> **Note for the implementer:** `BuildRouterWithCoverage`, `CollectAsync`, and `BuildGroundedAgentUpdates` may not exist verbatim — reuse this file's existing router-builder and chunk-collector helpers (there is already a `BuildRouter(params AnswerChunk[])`-style helper and an async-enumerable collector). Adapt these two tests to the file's actual helper names; the behavioral assertions (`DidNotReceive().GetAgent` for the zero-content case, `Received().GetAgent` for the has-content case) are the contract and must stay.

- [ ] **Step 6: Run the gate tests to verify they pass**

Run: `dotnet test tests/PinballWizard.Infrastructure.Tests --filter "FullyQualifiedName~AiRouterStreamingTests"`
Expected: PASS — including the two new tests and all pre-existing tests (which now pass `true` coverage and are unaffected).

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Application/Ai/IAiRouter.cs \
        src/PinballWizard.Application/Ai/AiRouter.cs \
        tests/PinballWizard.Infrastructure.Tests/Ai/AiRouterStreamingTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "feat(ai) machine-scope zero-content gate in AiRouter — skip agent on empty machine (ADR-0052)"
```

---

### Task 4: API — thread `machineId` through the ask-stream endpoint

**Files:**
- Modify: `src/PinballWizard.Api/Endpoints/WizardAskStreamEndpoint.cs` (request DTO lines 342–344; router call lines 202–204)
- Modify: `tests/PinballWizard.Web.Tests/Components/Wizard/WizardAskStreamEndpointTests.cs` (router substitute stubs the 4-arg overload; add a machineId round-trip test)

**Interfaces:**
- Consumes: `IAiRouter.AnswerStreamingAsync(question, history, machineId, ct)` (Task 3).
- Produces: `WizardAskRequest(string Question, IReadOnlyList<ConversationTurn>? History = null, string? MachineId = null)`.

- [ ] **Step 1: Add `MachineId` to the request DTO**

In `src/PinballWizard.Api/Endpoints/WizardAskStreamEndpoint.cs`, change the record (lines 342–344):

```csharp
internal sealed record WizardAskRequest(
    string Question,
    IReadOnlyList<ConversationTurn>? History = null,
    string? MachineId = null);
```

- [ ] **Step 2: Pass `MachineId` into the router call**

Change the router call (lines 202–204):

```csharp
        await foreach (var chunk in router
            .AnswerStreamingAsync(request.Question, request.History, request.MachineId, cancellationToken)
            .ConfigureAwait(false))
```

- [ ] **Step 3: Write the failing round-trip test**

In `tests/PinballWizard.Web.Tests/Components/Wizard/WizardAskStreamEndpointTests.cs`, update the `BuildRouter` helper to stub the 4-arg overload (add the `string?` machineId arg position):

```csharp
    private static IAiRouter BuildRouter(params AnswerChunk[] chunks)
    {
        var router = Substitute.For<IAiRouter>();
        router
            .AnswerStreamingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ConversationTurn>?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(chunks));
        return router;
    }
```

Then add a test asserting `machineId` round-trips from the JSON body to the router:

```csharp
    [Fact]
    public async Task Ask_WithMachineId_PassesMachineIdToRouter()
    {
        string? capturedMachineId = null;
        var router = Substitute.For<IAiRouter>();
        router
            .AnswerStreamingAsync(
                Arg.Any<string>(),
                Arg.Any<IReadOnlyList<ConversationTurn>?>(),
                Arg.Do<string?>(m => capturedMachineId = m),
                Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(new AnswerChunk.Final(
                new WizardAnswer(
                    Text: "ok", Citations: [], SubAgentUsed: "wizard",
                    Confidence: 1.0, Escalated: false, IsRefusal: false,
                    RefusalCategory: null, PromptVersion: "v1", FoundryThreadId: null))));

        using var server = BuildServer(router: router);
        using var client = server.CreateClient();

        var body = JsonSerializer.Serialize(
            new { question = "tell me about Super Flipp", machineId = "G4X1D-M2Yy1" }, JsonOptions);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/wizard/ask:stream", content);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("G4X1D-M2Yy1", capturedMachineId);
    }
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/PinballWizard.Web.Tests --filter "FullyQualifiedName~WizardAskStreamEndpointTests"`
Expected: PASS (existing tests still green after the 4-arg stub update; new round-trip test passes).

- [ ] **Step 5: Commit**

```bash
git add src/PinballWizard.Api/Endpoints/WizardAskStreamEndpoint.cs \
        tests/PinballWizard.Web.Tests/Components/Wizard/WizardAskStreamEndpointTests.cs
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "feat(api) thread machineId through wizard ask:stream endpoint (ADR-0052)"
```

---

### Task 5: Web — thread `machineId` from the detail-page button to the streaming client

**Files:**
- Modify: `src/PinballWizard.Web/Components/Wizard/IWizardStreamingClient.cs` (add a 3-arg canonical overload)
- Modify: `src/PinballWizard.Web/Components/Wizard/WizardStreamingClient.cs` (thread machineId into the JSON payload, lines 61–69 + 174–179)
- Modify: `src/PinballWizard.Web/Components/Wizard/WizardAnswerStream.razor` (add `MachineId` parameter, thread through `SubmitAsync`/`StreamAnswerAsync`, lines 210–211, 299–305, 318–364)
- Modify: `src/PinballWizard.Web/Components/Pages/Wizard.razor` (add `machineId` query param, forward to `<WizardAnswerStream>`, lines 39, 55–56)
- Modify: `src/PinballWizard.Web/Components/Shared/MachineDetail.razor` (append `&machineId=` in `OnAskWizardClick`, lines 320–325)

**Interfaces:**
- Consumes: the ask-stream endpoint's `machineId` body field (Task 4).
- Produces: `IWizardStreamingClient.StreamAsync(string question, IReadOnlyList<ConversationTurn>? history, string? machineId, CancellationToken ct)`.

- [ ] **Step 1: Extend `IWizardStreamingClient`**

In `src/PinballWizard.Web/Components/Wizard/IWizardStreamingClient.cs`, replace the two existing `StreamAsync` declarations with three overloads funnelling into a new 4-arg canonical method (mirroring the `IAiRouter` shape):

```csharp
    IAsyncEnumerable<AnswerChunk> StreamAsync(
        string question,
        CancellationToken cancellationToken)
        => StreamAsync(question, history: null, machineId: null, cancellationToken);

    IAsyncEnumerable<AnswerChunk> StreamAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        CancellationToken cancellationToken)
        => StreamAsync(question, history, machineId: null, cancellationToken);

    IAsyncEnumerable<AnswerChunk> StreamAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        string? machineId,
        CancellationToken cancellationToken);
```

- [ ] **Step 2: Thread `machineId` through `WizardStreamingClient`**

In `src/PinballWizard.Web/Components/Wizard/WizardStreamingClient.cs`:

Replace the single-turn shim (lines 61–64):

```csharp
    public IAsyncEnumerable<AnswerChunk> StreamAsync(
        string question,
        CancellationToken cancellationToken)
        => StreamAsync(question, history: null, machineId: null, cancellationToken);
```

Change the canonical `StreamAsync` (lines 66–69) to the 4-arg shape:

```csharp
    public async IAsyncEnumerable<AnswerChunk> StreamAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        string? machineId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
```

Update its call into `SendCoreAsync` to pass `machineId`, change `SendCoreAsync`'s signature to accept `string? machineId`, and change the payload object (lines 174–179):

```csharp
    using var request = new HttpRequestMessage(HttpMethod.Post, "/api/wizard/ask:stream")
    {
        Content = JsonContent.Create(
            new { question, history, machineId },
            options: SseJsonOptions),
    };
```

(A null `machineId` serializes away — `SseJsonOptions` sets `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull`.)

- [ ] **Step 3: Add the `MachineId` parameter to `WizardAnswerStream.razor`**

In `src/PinballWizard.Web/Components/Wizard/WizardAnswerStream.razor`:

After the `Question` parameter (lines 210–211) add:

```csharp
    [Parameter]
    public string? MachineId { get; set; }
```

In `SubmitAsync` (line ~340) change the `StreamAnswerAsync` call to forward `MachineId`:

```csharp
    await StreamAnswerAsync(_submittedQuestion, BuildHistory(), MachineId, _cts.Token).ConfigureAwait(false);
```

Change `StreamAnswerAsync`'s signature (lines 353–356) and its streaming-client call (lines 362–364):

```csharp
    private async Task StreamAnswerAsync(
        string question,
        IReadOnlyList<ConversationTurn>? history,
        string? machineId,
        CancellationToken ct)
    {
        var gotFirstChunk = false;

        try
        {
            await foreach (var chunk in StreamingClient
                .StreamAsync(question, history, machineId, ct)
                .ConfigureAwait(false))
```

- [ ] **Step 4: Add the `machineId` query param to `Wizard.razor`**

In `src/PinballWizard.Web/Components/Pages/Wizard.razor`:

After the `Q` query param (lines 55–56) add:

```csharp
    [SupplyParameterFromQuery(Name = "machineId")]
    public string? MachineId { get; set; }
```

Change the `<WizardAnswerStream>` usage (line 39):

```razor
<WizardAnswerStream Question="@_resolvedQuestion" MachineId="@MachineId" />
```

- [ ] **Step 5: Append `machineId` in the detail-page button**

In `src/PinballWizard.Web/Components/Shared/MachineDetail.razor`, change `OnAskWizardClick` (lines 320–325):

```csharp
    private void OnAskWizardClick()
    {
        if (_machine is null) return;
        var query = $"tell me about {_machine.Title}";
        Nav.NavigateTo(
            $"/wizard?q={Uri.EscapeDataString(query)}&machineId={Uri.EscapeDataString(_machine.Id)}");
    }
```

- [ ] **Step 6: Build the Web project to verify it compiles**

Run: `dotnet build src/PinballWizard.Web`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add src/PinballWizard.Web/Components/Wizard/IWizardStreamingClient.cs \
        src/PinballWizard.Web/Components/Wizard/WizardStreamingClient.cs \
        src/PinballWizard.Web/Components/Wizard/WizardAnswerStream.razor \
        src/PinballWizard.Web/Components/Pages/Wizard.razor \
        src/PinballWizard.Web/Components/Shared/MachineDetail.razor
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "feat(web) pass machineId from Ask-the-Wizard button through to the router (ADR-0052)"
```

---

### Task 6: ADR status flip + full-suite verification

**Files:**
- Modify: `docs/adr/0052-deterministic-zero-content-shortcircuit.md` (Status: Proposed → Accepted)
- Modify: `docs/adr/README.md` (index row status Proposed → Accepted)

- [ ] **Step 1: Flip the ADR status**

In `docs/adr/0052-deterministic-zero-content-shortcircuit.md`, change `**Status:** Proposed` to `**Status:** Accepted`.
In `docs/adr/README.md`, change the `0052` row's trailing `| Proposed |` to `| Accepted |`.

- [ ] **Step 2: Run the full CI-equivalent suite**

Run: `dotnet test PinballWizard.slnx --filter "Category!=Accessibility&Category!=Snapshots&Category!=Circuit&Category!=E2E"`
Expected: PASS (all projects). Investigate and fix any failure before proceeding — do not adjust test categories or filters to make it pass.

- [ ] **Step 3: Commit**

```bash
git add docs/adr/0052-deterministic-zero-content-shortcircuit.md docs/adr/README.md
git -c user.name="Jim Keeley" -c user.email="94459922+jkeeley2073@users.noreply.github.com" \
  commit -m "docs(adr) 0052 Accepted — machine-scope zero-content gate shipped"
```

- [ ] **Step 4: Pre-push self-audit (per repo PR-AUDIT)**

Run `/local-review` and `/standards-audit` against the branch diff. Treat 🔴 as blocking. Record both outcomes in the PR description when the PR is opened.

---

## Verification summary (what "done" looks like)

- A machine-scoped ask (`machineId` present) for a machine with **zero** indexed chunks returns the community-resource refusal with **no** `GetAgent` call — proven by `MachineScopedAsk_WithZeroIndexedChunks_ShortCircuits_WithoutInvokingAgent`.
- A machine-scoped ask for a machine with **≥1** chunk still invokes the agent — proven by `MachineScopedAsk_WithIndexedChunks_InvokesAgent_DoesNotShortCircuit` (the regression guard against suppressing metadata-grounded answers).
- The coverage count filter is byte-identical to the retriever's machine filter — proven by `AiSearchMachineCorpusCoverageTests` (incl. the apostrophe-escaping case).
- `machineId` round-trips from the button → query string → JSON body → router — proven by `Ask_WithMachineId_PassesMachineIdToRouter`.
- A coverage-check failure meters `AiMachineScopeGateErrors` and falls through to the agent (no masking) — covered by the try/catch in Task 3 Step 4.
- Full CI-equivalent suite green.

## Observability note (refinement from the approved design)

The approved design mentioned tagging the short-circuit counter by `manufacturer` + `had_doc_links`. During planning these were dropped to avoid contract/coupling smells, and the reasons are recorded here:

- **`manufacturer`** is not available at the gate point without an extra Cosmos read (a zero-chunk machine has no manufacturer in the index) or a metric-only API field. The per-fire **structured log carries `machineId`**, from which manufacturer is resolvable offline. Adding a live manufacturer tag is a one-line follow-up if the rate justifies it.
- **`had_doc_links`** (the "linked docs but zero chunks = indexing lag" signal) is already covered by the existing `CosmosAiSearchRagReconciler` `Missing`-drift classification — the gate should not duplicate it.

If a live manufacturer breakdown is wanted, raise it during plan review and it will be added (source: thread `manufacturerKey` from the detail page, or a machine point-read on the gate's cheap path).
