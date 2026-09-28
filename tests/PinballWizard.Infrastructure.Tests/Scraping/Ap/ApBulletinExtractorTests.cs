using PinballWizard.Infrastructure.Scraping.Ap;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.Ap;

// Driven by the per-game support pages captured 2026-09-28 (TEST-05; see
// Fixtures/Ap/CAPTURE.md). Expected URLs are literals read off the capture,
// not re-derived by the code under test.
public sealed class ApBulletinExtractorTests
{
    private static readonly Uri HoudiniSupportUrl = new("https://americanpinball.com/support/houdini/");
    private static readonly Uri BarrySupportUrl = new("https://americanpinball.com/support/barry-os-bbq-challenge/");
    private static readonly Uri SupportIndexUrl = new("https://americanpinball.com/support/");
    private static readonly string[] BulletinCategories = ["service-bulletin", "electrical"];

    private const string HubFs = "https://48804760.fs1.hubspotusercontent-na1.net/hubfs/48804760/Support%20Files/";

    [Fact]
    public void ExtractBulletins_CapturedHoudiniPage_CollectsServiceBulletinAndElectricalPdfs()
    {
        var extraction = ApBulletinExtractor.ExtractBulletins(
            ApFixtures.Read("support-houdini.captured.html"), HoudiniSupportUrl, "houdini", BulletinCategories);

        string[] expected =
        [
            HubFs + "Service%20Bulletin/UNIVERSAL%20-%20USB%20drive%20formatting%20procedure.pdf",
            HubFs + "Service%20Bulletin/Houdini%20Speaker%20Grill%20Installation.pdf",
            HubFs + "Service%20Bulletin/Houdini%20-%20Skill%20Shot%20Fix.pdf",
            HubFs + "Service%20Bulletin/Houdini%20-%20Scoop%20Adjustment%20for%20Straight%20Down%20the%20Middle%20Drains.pdf",
            HubFs + "Electrical/Houdini%20-%20Topper%20Installation%20Instructions.pdf",
            HubFs + "Electrical/Houdini%20-%20Shaker%20Motor%20Installation.pdf",
            HubFs + "Electrical/Houdini%20-%20Power%20Supply%20Kit%20Installation.pdf",
            HubFs + "Electrical/Houdini%20-%20Knocker%20Kit%20Installation%20Guide.pdf",
            HubFs + "Electrical/Houdini%20-%20EOS%20Installation.pdf",
            HubFs + "Electrical/Houdini%20-%20Coil%20Performance%20Improvement%20Kit.pdf",
        ];

        // Every card links its PDF twice (icon + "Download"), so an exact
        // count of ten also proves the per-page dedup fired.
        Assert.Equal(expected, extraction.Links.Select(l => l.FileUrl).ToArray());
        Assert.All(extraction.Links, link =>
        {
            Assert.Equal("houdini", link.GameSlug);
            Assert.Equal("American Pinball Support Page", link.DiscoveryContext);
        });
        Assert.Equal(16, extraction.PostCount);
        Assert.Equal(12, extraction.BulletinPostCount);
        Assert.Equal(2, extraction.PostsWithoutDocument);
        Assert.Empty(extraction.RejectedHosts);
    }

    [Fact]
    public void ExtractBulletins_CapturedHoudiniPage_LeavesManualsCodeUpdatesAndFlyerToTheGamePageScraper()
    {
        var extraction = ApBulletinExtractor.ExtractBulletins(
            ApFixtures.Read("support-houdini.captured.html"), HoudiniSupportUrl, "houdini", BulletinCategories);

        var urls = extraction.Links.Select(l => l.FileUrl).ToList();
        Assert.DoesNotContain(urls, u => u.Contains("Game%20Manuals", StringComparison.Ordinal));
        Assert.DoesNotContain(urls, u => u.Contains("Quick%20Reference%20Guides", StringComparison.Ordinal));
        Assert.DoesNotContain(urls, u => u.Contains("Release%20Notes", StringComparison.Ordinal));
        Assert.DoesNotContain(urls, u => u.EndsWith("Houdini-Pinball-Flyer.pdf", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractBulletins_CapturedHoudiniPage_TitlesEachLinkFromItsCardHeadings()
    {
        var extraction = ApBulletinExtractor.ExtractBulletins(
            ApFixtures.Read("support-houdini.captured.html"), HoudiniSupportUrl, "houdini", BulletinCategories);

        var byUrl = extraction.Links.ToDictionary(l => l.FileUrl, l => l.LinkText);
        Assert.Equal("Houdini - Skill Shot Fix", byUrl[HubFs + "Service%20Bulletin/Houdini%20-%20Skill%20Shot%20Fix.pdf"]);
        Assert.Equal(
            "General - USB Drive Formatting Procedure",
            byUrl[HubFs + "Service%20Bulletin/UNIVERSAL%20-%20USB%20drive%20formatting%20procedure.pdf"]);
        Assert.Equal("Houdini - Knocker Installation", byUrl[HubFs + "Electrical/Houdini%20-%20Knocker%20Kit%20Installation%20Guide.pdf"]);
    }

    [Fact]
    public void ExtractBulletins_CapturedBarryOsPage_HasPostCardsButNoBulletinPosts()
    {
        var extraction = ApBulletinExtractor.ExtractBulletins(
            ApFixtures.Read("support-barry-os-bbq-challenge.captured.html"),
            BarrySupportUrl,
            "barry-os-bbq-challenge",
            BulletinCategories);

        Assert.Empty(extraction.Links);
        Assert.Equal(3, extraction.PostCount);
        Assert.Equal(0, extraction.BulletinPostCount);
    }

    [Fact]
    public void ExtractBulletins_CapturedSupportIndex_NoLongerListsAnyBulletinPdf()
    {
        // The regression behind the 2026-09-27 job failure: the redesigned
        // /support/ index links to per-game hubs and carries no bulletin PDFs,
        // on s4.american-pinball.com or anywhere else.
        var html = ApFixtures.Read("support-index.captured.html");
        var extraction = ApBulletinExtractor.ExtractBulletins(html, SupportIndexUrl, "support", BulletinCategories);

        Assert.DoesNotContain("s4.american-pinball.com", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".pdf", html, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(extraction.Links);
        Assert.Equal(0, extraction.BulletinPostCount);
    }

    [Fact]
    public void ExtractBulletins_BulletinPdfsOnForeignHost_RecordsHostAndKeepsNoLink()
    {
        // The captured page with its HubSpot file host swapped out: same
        // markup, every bulletin PDF now on a host outside the allow-list.
        var html = ApFixtures.Read("support-houdini.captured.html")
            .Replace("48804760.fs1.hubspotusercontent-na1.net", "files.example.net", StringComparison.Ordinal);

        var extraction = ApBulletinExtractor.ExtractBulletins(html, HoudiniSupportUrl, "houdini", BulletinCategories);

        Assert.Empty(extraction.Links);
        Assert.Equal(12, extraction.BulletinPostCount);
        Assert.Equal(["files.example.net"], extraction.RejectedHosts.ToArray());
    }

    [Fact]
    public void ExtractBulletins_OnlyServiceBulletinCategory_ExcludesElectricalCards()
    {
        var extraction = ApBulletinExtractor.ExtractBulletins(
            ApFixtures.Read("support-houdini.captured.html"), HoudiniSupportUrl, "houdini", ["service-bulletin"]);

        Assert.Equal(4, extraction.Links.Count);
        Assert.Equal(6, extraction.BulletinPostCount);
        Assert.DoesNotContain(extraction.Links, l => l.FileUrl.Contains("/Electrical/", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractBulletins_EmptyHtml_ReportsNoPostCards()
    {
        var extraction = ApBulletinExtractor.ExtractBulletins(string.Empty, HoudiniSupportUrl, "houdini", BulletinCategories);

        Assert.Empty(extraction.Links);
        Assert.Equal(0, extraction.PostCount);
    }
}
