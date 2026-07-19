using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;
using PinballWizard.Web.Components.Pages.Admin;
using PinballWizard.Web.Security;
using System.Security.Claims;
using Xunit;

namespace PinballWizard.Web.Tests.Admin;

// bUnit tests for AdminLinkReview — /admin/link-review needs-review queue.
//
// Covers:
//   (1) a NeedsReview doc with 2 candidates renders both candidates with evidence;
//       clicking "Assign" writes a Tier-0 link_overrides record and flips the doc to Pending.
//   (2) when the queue is empty, the AppEmptyState is rendered.
//   (3) a non-admin viewer never sees the Assign button — AdminActionGuard is the real
//       gate, but _isAdmin governs what renders in the first place.
//   (4) when the override write fails, the row survives (nothing removed from the
//       queue) and the status flip never fires — the two-step write in AssignAsync
//       must not silently continue past a failed first step.
//
// Pattern: RenderWithPopover<T> (base class helper) for MudBlazor 9 popover requirement.
// Admin-gate: IAuthorizationService mocked per-test via _authService so individual
// tests can flip Success/Failed without touching the shared constructor wiring.
//
// ADR-0046 — AppDataGrid / AppEmptyState (never raw MudTable)
// ADR-0054 — NeedsReview status + admin queue
public sealed class LinkReviewTests : AsyncBunitContext
{
    private readonly IRawDocumentRepository _rawDocRepo;
    private readonly ILinkOverrideRepository _overrideRepo;
    private readonly IAuthorizationService _authService;

    public LinkReviewTests()
    {
        _rawDocRepo = Substitute.For<IRawDocumentRepository>();
        _overrideRepo = Substitute.For<ILinkOverrideRepository>();

        // IAuthorizationService: always approve by default so AdminActionGuard returns
        // true and the Assign button renders — individual tests may re-Returns() this
        // to Failed() to exercise the non-admin path.
        _authService = Substitute.For<IAuthorizationService>();
        _authService
            .AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());

        Services.AddMudServices();
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        Services.AddSingleton(_rawDocRepo);
        Services.AddSingleton(_overrideRepo);
        Services.AddSingleton(_authService);
        Services.AddSingleton<AdminActionGuard>();

        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public async Task LinkReview_RendersCandidates_AndResolvingWritesAnOverride()
    {
        // Arrange: one NeedsReview doc with 2 candidates
        var doc = MakeNeedsReviewDoc("doc_abc123", candidateCount: 2);
        SetupStream([doc]);

        var cut = RenderWithPopover<AdminLinkReview>();

        // Wait for the async OnAfterRenderAsync data load to complete
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Test Machine 0", cut.Markup);
            Assert.Contains("Test Machine 1", cut.Markup);
        }, timeout: TimeSpan.FromSeconds(3));

        // Both candidates appear with their evidence kind
        Assert.Contains("game_title", cut.Markup);

        // Act: click "Assign" on the first candidate's row. The testid is keyed by
        // (DocumentId, MachineId) rather than a positional row index, so it stays
        // stable across re-renders that remove other rows (e.g. a prior Assign).
        var assignButton = cut.Find("[data-testid='link-review-assign-doc_abc123-mch_000']");
        await cut.InvokeAsync(() => assignButton.Click());

        // Assert: link_overrides upsert was called with the first candidate's machine ID.
        // NSubstitute's Received() verification call returns the mocked Task from the
        // interface signature — it is not meant to be awaited (the assertion already ran
        // synchronously by the time Received() returns), so discard it explicitly rather
        // than let CS4014 flag a real fire-and-forget bug.
        _ = _overrideRepo.Received(1).UpsertAsync(
            Arg.Is<LinkOverrideRecord>(r =>
                r.MachineIds.Length == 1 &&
                r.MachineIds[0] == "mch_000"),
            Arg.Any<CancellationToken>());

        // Assert: doc was flipped back to Pending so the linker re-processes it
        _ = _rawDocRepo.Received(1).UpdateLinkStatusAsync(
            "doc_abc123",
            LinkStatus.Pending,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void LinkReview_NoPendingReviews_ShowsEmptyState()
    {
        // Arrange: empty queue
        SetupStream([]);

        var cut = RenderWithPopover<AdminLinkReview>();

        // Wait for load and empty state
        cut.WaitForAssertion(() =>
        {
            var empty = cut.Find("[data-testid='link-review-empty']");
            Assert.NotNull(empty);
        }, timeout: TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void LinkReview_NotAdmin_DoesNotRenderAssignButton()
    {
        // Arrange: authorization denied — AdminActionGuard.IsAdminAsync resolves false.
        _authService
            .AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Failed());

        var doc = MakeNeedsReviewDoc("doc_notadmin", candidateCount: 1);
        SetupStream([doc]);

        var cut = RenderWithPopover<AdminLinkReview>();

        // Wait for the candidate to render, then assert no Assign button exists for it —
        // _isAdmin gates the button's very presence, not just whether it is clickable.
        cut.WaitForAssertion(() => Assert.Contains("Test Machine 0", cut.Markup), timeout: TimeSpan.FromSeconds(3));
        Assert.Empty(cut.FindAll("[data-testid='link-review-assign-doc_notadmin-mch_000']"));
    }

    [Fact]
    public async Task LinkReview_OverrideWriteFails_LeavesRowInQueue_AndNeverFlipsStatus()
    {
        // Arrange: the override upsert throws — the first half of AssignAsync's two-step
        // write. The status flip must never fire, and the row must stay in the queue so
        // the admin can retry (both writes are upserts; a full retry is always safe).
        var doc = MakeNeedsReviewDoc("doc_err", candidateCount: 1);
        SetupStream([doc]);
        _overrideRepo
            .When(x => x.UpsertAsync(Arg.Any<LinkOverrideRecord>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new InvalidOperationException("Cosmos write failed"));

        var cut = RenderWithPopover<AdminLinkReview>();
        cut.WaitForAssertion(() => Assert.Contains("Test Machine 0", cut.Markup), timeout: TimeSpan.FromSeconds(3));

        var assignButton = cut.Find("[data-testid='link-review-assign-doc_err-mch_000']");
        await cut.InvokeAsync(() => assignButton.Click());

        // The row survives the failed assign — nothing was removed from the queue, and
        // the Assign button re-renders (ActionBusy reset in `finally`) rather than
        // staying stuck on the busy spinner.
        cut.WaitForAssertion(
            () => Assert.NotEmpty(cut.FindAll("[data-testid='link-review-assign-doc_err-mch_000']")),
            timeout: TimeSpan.FromSeconds(3));

        // UpdateLinkStatusAsync must never fire — the override write failed first, so
        // the document must not be flipped to Pending on top of a missing override.
        _ = _rawDocRepo.DidNotReceive().UpdateLinkStatusAsync(
            Arg.Any<string>(),
            Arg.Any<LinkStatus>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static RawDocumentRecord MakeNeedsReviewDoc(string documentId, int candidateCount)
    {
        var candidates = Enumerable.Range(0, candidateCount)
            .Select(i => new LinkReviewCandidate
            {
                MachineId = $"mch_{i:000}",
                MachineTitle = $"Test Machine {i}",
                EvidenceKind = "game_title",
                MatchedVariant = $"machine_{i}",
            })
            .ToList();

        return new RawDocumentRecord
        {
            DocumentId = documentId,
            DocumentUrl = "https://example.com/manual.pdf",
            DocumentType = DocumentType.Manual,
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://example.com/game/foo/",
                DiscoveryContext = "Game Page",
                FileUrl = "https://example.com/manual.pdf",
            },
            Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
            LinkStatus = LinkStatus.NeedsReview,
            Manufacturer = "Acme Pinball",
            LinkReview = new LinkReviewInfo
            {
                Candidates = candidates,
                CreatedAt = DateTime.UtcNow,
            },
        };
    }

    private void SetupStream(IReadOnlyList<RawDocumentRecord> docs)
    {
        _rawDocRepo
            .StreamByStatusAsync(
                Arg.Any<IReadOnlyCollection<LinkStatus>>(),
                Arg.Any<CancellationToken>())
            .Returns(AsAsyncEnumerable(docs));
    }

#pragma warning disable CS1998 // sync-only iterator — async envelope for IAsyncEnumerable<T>
    private static async IAsyncEnumerable<T> AsAsyncEnumerable<T>(IReadOnlyList<T> items)
    {
        foreach (var item in items)
            yield return item;
    }
#pragma warning restore CS1998
}
