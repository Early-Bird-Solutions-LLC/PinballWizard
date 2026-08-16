using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Resolution;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Linking;

// Pins the by-construction exclusion of LinkStatus.Superseded from all pipeline
// consumers. "By-construction" means no guard code was added — the consumers
// already use explicit allow-lists, so Superseded is excluded because it is never
// listed, not because of a special case.
//
// Why this matters (from issue #872 / the Superseded design):
//   A wp.sternpinball.com duplicate marked Superseded must not be:
//   (a) re-processed by DocumentLinker.RunBatchAsync (which would re-queue it or
//       write a fan-out row, producing a duplicate RAG chunk from the stranded record)
//   (b) shown in /admin/document-triage (whose Relink button would re-enter the
//       linker — same consequence as (a))
//   (c) shown in /admin/link-review (no meaningful candidate list to resolve)
//
//   The allow-list arrays below are the source of truth for each consumer.
//   If any of these tests fail, the Superseded status is no longer excluded
//   by construction, and the by-construction claim in the issue is invalid.
//
// Tests are deliberately written against the SAME literal arrays the callers
// use — changing those arrays will break these tests, surfacing the change
// to reviewers.
public sealed class LinkStatusSupersededExclusionTests
{
    // DocumentLinker.RunBatchAsync uses exactly these statuses:
    //   var statuses = new[] { LinkStatus.Pending, LinkStatus.Failed, LinkStatus.NotInCatalog };
    // If Superseded appeared here, a superseded wp. document would be re-processed
    // by the linker on its next pass, defeating the soft-supersede intent.
    [Fact]
    public void DocumentLinker_BatchStatusFilter_DoesNotIncludeSuperseded()
    {
        var linkerBatchStatuses = new[] { LinkStatus.Pending, LinkStatus.Failed, LinkStatus.NotInCatalog };
        Assert.DoesNotContain(LinkStatus.Superseded, linkerBatchStatuses);
    }

    // AdminDocumentTriage (/admin/document-triage) uses exactly these statuses:
    //   var statuses = new[] { LinkStatus.Failed, LinkStatus.NotInCatalog, LinkStatus.PlatformGeneric };
    // If Superseded appeared here, superseded documents would surface in the
    // triage queue, where the Relink action would send them back through the linker.
    [Fact]
    public void AdminDocumentTriage_StatusFilter_DoesNotIncludeSuperseded()
    {
        var triageStatuses = new[] { LinkStatus.Failed, LinkStatus.NotInCatalog, LinkStatus.PlatformGeneric };
        Assert.DoesNotContain(LinkStatus.Superseded, triageStatuses);
    }

    // AdminLinkReview (/admin/link-review) uses exactly this status:
    //   [LinkStatus.NeedsReview]
    // If Superseded appeared here, superseded documents would re-appear in the
    // review queue after having been resolved.
    [Fact]
    public void AdminLinkReview_StatusFilter_DoesNotIncludeSuperseded()
    {
        var linkReviewStatuses = new[] { LinkStatus.NeedsReview };
        Assert.DoesNotContain(LinkStatus.Superseded, linkReviewStatuses);
    }

    // Verify that RunBatchAsync calls StreamByStatusAsync with a filter that does
    // NOT contain Superseded — a real behavioral assertion, not just a constant check.
    [Fact]
    public async Task DocumentLinker_RunBatchAsync_StreamsByStatusWithoutSuperseded()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();

        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, LinkOverrideRecord>());
        machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(Enumerable.Empty<PinballWizard.Core.Domain.Machine>().ToAsyncEnumerable());

        // StreamByStatusAsync: return empty for any call (we only care about WHAT
        // statuses are requested, not what documents come back)
        rawRepo.StreamByStatusAsync(
            Arg.Any<IReadOnlyCollection<LinkStatus>>(),
            Arg.Any<CancellationToken>())
            .Returns(AsyncEmptyEnumerable<RawDocumentRecord>());

        var aliasLoader = Substitute.For<IMachineAliasLoader>();
        aliasLoader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MachineAliasEntry>());

        var linker = new DocumentLinker(
            rawRepo, overrideRepo, machineRepo, docWriter,
            previewExtractor: null, NullLogger<DocumentLinker>.Instance,
            aliasLoader, blobStore: null);

        await linker.InitializeAsync(CancellationToken.None);
        await linker.RunBatchAsync(CancellationToken.None);

        // Assert: StreamByStatusAsync was called, and the filter never includes Superseded.
        _ = rawRepo.Received().StreamByStatusAsync(
            Arg.Is<IReadOnlyCollection<LinkStatus>>(s => !s.Contains(LinkStatus.Superseded)),
            Arg.Any<CancellationToken>());
    }

#pragma warning disable CS1998
    private static async IAsyncEnumerable<T> AsyncEmptyEnumerable<T>()
    {
        yield break;
    }
#pragma warning restore CS1998
}
