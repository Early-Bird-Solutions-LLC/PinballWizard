using PinballWizard.Application;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Documents;

// Driven by the captured AP support-page fixture (TEST-05).  Every URL in the
// captured list must either classify to an indexable DocumentType or be a
// known game-agnostic platform doc whose Other classification is deliberate.
//
// The fixture was captured 2026-07-13 from https://www.american-pinball.com/support/
// by the branch fix/ap-bulletins-real-patterns — see
// tests/PinballWizard.Infrastructure.Tests/Fixtures/Ap/CAPTURE.md for the
// full provenance record.
public sealed class ApDocumentClassificationTests
{
    private const string ApSupportContext = "American Pinball Support Page";

    private static DiscoveredLink Link(string url) =>
        new() { FileUrl = url, LinkText = null };

    private static readonly string CapturedApUrlList = Path.Combine(
        RepoRoot(),
        "tests",
        "PinballWizard.Infrastructure.Tests",
        "Fixtures",
        "Ap",
        "bulletin-urls.captured.txt");

    // Filenames of genuinely game-agnostic captured docs that correctly remain
    // Other (RAG skips them — they are hardware/platform docs, not game manuals
    // or service bulletins tied to a title).  Enumerated from the captured list;
    // do not guess.
    private static readonly HashSet<string> KnownGenericFilenames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Power-Distribution.pdf",
            "SCOOP-ADJUSTMENT.pdf",
            "Shaker.pdf",
            "Add-On-Board.pdf",
            "End-Of-Stroke-Switch.pdf",
            "New-Add-On-Board.pdf",
            "Assembly.pdf",
            "Bar-Door-Check.pdf",
            "Coil-Stop-BBR.pdf",
            "DOC0018-00-REV-A.pdf",
            "HW-car-attachment-instructions[6658].pdf",
            "Overseas-48v-Power-Supply-Supplement.pdf",
            "Hot-Wheels--audio-connection-and-settings.pdf",
            "DBA-for-LOV.pdf",
            "USB-drive-formatting-procedure.pdf",
            "12v-DC-and-120v-AC-Dollar-Bill-Acceptor-connector-7-12-21.pdf",
        };

    private static bool IsKnownGenericDoc(string url) =>
        KnownGenericFilenames.Contains(Path.GetFileName(url));

    // ── Named examples from the captured list ─────────────────────────────
    //
    // These tests pass SourceType.GamePage (a mixed-content type with no
    // blanket mapping) to exercise the URL/filename heuristics rather than
    // the source-type shortcut. That exercises the heuristic path that
    // serves the reclassifier (which has source_type from stored records
    // and can use the shortcut) and validates the heuristics remain correct.

    [Theory]
    [InlineData("Houdini--Quick-Reference-Guide.pdf", DocumentType.Manual)]
    [InlineData("API-Houdini-Service-Manual-10-6-21.pdf", DocumentType.Manual)]
    [InlineData("Galactic-Tank-Force-Game-Manual-(Version-1.0_October-2023).pdf", DocumentType.Manual)]
    [InlineData("Okto-english-manual-10-5-21.pdf", DocumentType.Manual)]
    [InlineData("Hot-Wheels-Manual-10-14-2021.pdf", DocumentType.Manual)]
    [InlineData("Houdini-Skill-Shot-Fix.pdf", DocumentType.ServiceBulletin)]
    [InlineData("Hotwheels-GI-EPIC-3-Wire-update.pdf", DocumentType.ServiceBulletin)]
    [InlineData("Houdini--Coil-Performance-Improvement-Kit.pdf", DocumentType.ServiceBulletin)]
    public void ClassifyDocumentType_ApSupportDocument_IsIndexable(string filename, DocumentType expected)
    {
        var actual = ScraperOrchestrator.ClassifyDocumentType(
            Link($"http://s4.american-pinball.com/img/support/2021-11/{filename}"),
            ApSupportContext,
            SourceType.GamePage);

        Assert.Equal(expected, actual);
    }

    // ── Full captured-list sweep ───────────────────────────────────────────

    [Fact]
    public void ClassifyDocumentType_AllCapturedApUrls_AreIndexableOrKnownGenericDocs()
    {
        var urls = File.ReadAllLines(CapturedApUrlList);
        Assert.NotEmpty(urls);

        foreach (var url in urls)
        {
            var t = ScraperOrchestrator.ClassifyDocumentType(Link(url), ApSupportContext, SourceType.GamePage);
            Assert.True(
                t != DocumentType.Other || IsKnownGenericDoc(url),
                $"{url} classified Other and is not a known generic/platform doc — RAG would skip it.");
        }
    }

    // Issue #815: the live AP scraper sets source_type=ServiceBulletinPage.
    // Documents with bare titles that contain no keyword (e.g. "Bar Door Check",
    // "Tank Treads Installation") must classify as ServiceBulletin via the
    // source-type shortcut so they are admitted to RAG ingestion.
    [Theory]
    [InlineData("Bar-Door-Check.pdf", "Bar Door Check")]
    [InlineData("Tank-Treads-Installation.pdf", "Tank Treads Installation")]
    public void ClassifyDocumentType_ApWithServiceBulletinPageSourceType_ReturnsServiceBulletin(string filename, string linkText)
    {
        var link = new DiscoveredLink
        {
            FileUrl = $"http://s4.american-pinball.com/img/support/2022-04/{filename}",
            LinkText = linkText,
        };

        var actual = ScraperOrchestrator.ClassifyDocumentType(link, ApSupportContext, SourceType.ServiceBulletinPage);

        Assert.Equal(DocumentType.ServiceBulletin, actual);
    }

    // ── The AP heuristics must not leak to other manufacturers ─────────────
    //
    // The fix/update/improvement/kit/install words are evidence of a bulletin
    // only because AP's real filenames use them. As a global substring test they
    // misfire badly, so they are host-gated and token-matched. These cases pin
    // that: each would classify ServiceBulletin under the naive rule.

    [Theory]
    // Same filename shapes, different manufacturer → must NOT become a bulletin.
    [InlineData("https://sternpinball.com/files/Godzilla-software-update.pdf")]
    [InlineData("https://spookypinball.com/files/Halloween-kit-list.pdf")]
    [InlineData("https://pinballbrothers.com/docs/Alien-prefix-notes.pdf")]
    public void ClassifyDocumentType_NonApUrl_DoesNotInheritApBulletinHeuristics(string url)
    {
        var actual = ScraperOrchestrator.ClassifyDocumentType(Link(url), "Support", SourceType.GamePage);
        Assert.NotEqual(DocumentType.ServiceBulletin, actual);
    }

    [Theory]
    // On the AP host, but the keyword only appears INSIDE a longer word.
    // Substring matching would call all three bulletins; token matching must not.
    [InlineData("prefix-table.pdf")]
    [InlineData("suffix-chart.pdf")]
    [InlineData("kitchen-cabinet-artwork.pdf")]
    public void ClassifyDocumentType_ApUrl_SubstringOnlyKeyword_IsNotABulletin(string filename)
    {
        var actual = ScraperOrchestrator.ClassifyDocumentType(
            Link($"http://s4.american-pinball.com/img/support/2021-11/{filename}"),
            ApSupportContext,
            SourceType.GamePage);

        Assert.NotEqual(DocumentType.ServiceBulletin, actual);
    }

    [Theory]
    // Real captured filenames whose bulletin token is a whole word — these MUST
    // still classify, including the "Installation" inflection of "install".
    [InlineData("Knocker-Installation.pdf")]
    [InlineData("HWL--shaker-install.pdf")]
    [InlineData("Power-Supply-Kit-Installation.pdf")]
    public void ClassifyDocumentType_ApUrl_WholeTokenKeyword_IsABulletin(string filename)
    {
        var actual = ScraperOrchestrator.ClassifyDocumentType(
            Link($"http://s4.american-pinball.com/img/support/2021-11/{filename}"),
            ApSupportContext,
            SourceType.GamePage);

        Assert.Equal(DocumentType.ServiceBulletin, actual);
    }

    // ── Repo-root locator (same pattern as CrossPartitionQueryAllowListTests) ─

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            "Could not locate repo root (no PinballWizard.slnx found walking up from test assembly).");
    }
}
