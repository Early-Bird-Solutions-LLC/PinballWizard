using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Resolution;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Linking;

// Pins the by-construction exclusion of LinkStatus.Superseded from the linker.
// "By-construction" means no guard was added for it — DocumentLinker already
// queries an explicit allow-list, so a superseded document is skipped because it
// is never requested, not because of a special case.
//
// Why it matters (#872): a wp.sternpinball.com duplicate marked Superseded must not
// be re-processed by RunBatchAsync, which would re-queue it or write a fan-out row
// and ultimately produce a duplicate RAG chunk from the stranded record.
//
// NOTE ON TEST DESIGN — this file previously also contained three tests shaped like:
//
//     var statuses = new[] { LinkStatus.Pending, LinkStatus.Failed, LinkStatus.NotInCatalog };
//     Assert.DoesNotContain(LinkStatus.Superseded, statuses);
//
// They were deleted. That array is declared IN THE TEST, so the assertion is a
// tautology about a local literal and never touches production code. Proven by
// mutation: adding LinkStatus.Superseded to DocumentLinker's real filter left all
// three passing and only the substitute-based test below failed. A test that cannot
// fail is worse than no test, because it reports coverage it does not provide — the
// #758 defect class. The page-level equivalents now live in the Web tests, where the
// component is actually rendered.
public sealed class LinkStatusSupersededExclusionTests
{
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

        // Return empty for any call — this test asserts WHICH statuses are requested,
        // not what comes back.
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

        // Verified to fail when Superseded is added to the linker's real status array.
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
