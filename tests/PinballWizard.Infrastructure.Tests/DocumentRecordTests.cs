using PinballWizard.Application;
using PinballWizard.Application.Downloading;
using PinballWizard.Core.Scraping;
using PinballWizard.Infrastructure.Downloading;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Scraper.Tests;

public class DocumentRecordTests
{
    [Fact]
    public void GenerateId_SameUrl_ReturnsSameId()
    {
        var url = "https://sternpinball.com/wp-content/uploads/2020/01/StrangerThings_Pro_web.pdf";
        var id1 = DocumentRecord.GenerateId(url);
        var id2 = DocumentRecord.GenerateId(url);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void GenerateId_IsCaseInsensitive()
    {
        var id1 = DocumentRecord.GenerateId("https://sternpinball.com/File.PDF");
        var id2 = DocumentRecord.GenerateId("https://sternpinball.com/file.pdf");

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void GenerateId_DifferentUrls_ReturnDifferentIds()
    {
        var id1 = DocumentRecord.GenerateId("https://sternpinball.com/file1.pdf");
        var id2 = DocumentRecord.GenerateId("https://sternpinball.com/file2.pdf");

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void GenerateId_StartsWithDocPrefix()
    {
        var id = DocumentRecord.GenerateId("https://example.com/test.pdf");
        Assert.StartsWith("doc_", id);
    }
}

public class FileOrganizerTests
{
    [Fact]
    public void GetLocalPath_ManualsPage_ReturnsManualPath()
    {
        var path = FileOrganizer.GetLocalPath(
            "https://sternpinball.com/wp-content/uploads/StrangerThings_Pro_web.pdf",
            SourceType.ManualsPage);

        Assert.Equal(Path.Combine("manuals", "StrangerThings_Pro_web.pdf"), path);
    }

    [Fact]
    public void GetLocalPath_GamePage_ReturnsGamePath()
    {
        var path = FileOrganizer.GetLocalPath(
            "https://sternpinball.com/wp-content/uploads/firmware.zip",
            SourceType.GamePage,
            gameSlug: "stranger-things",
            tab: "GameCode");

        Assert.Equal(Path.Combine("games", "stranger-things", "game-code", "firmware.zip"), path);
    }

    [Fact]
    public void GetLocalPath_ServiceBulletin_ReturnsBulletinPath()
    {
        var path = FileOrganizer.GetLocalPath(
            "https://sternpinball.com/wp-content/uploads/sb174.pdf",
            SourceType.ServiceBulletinPage);

        Assert.Equal(Path.Combine("service-bulletins", "sb174.pdf"), path);
    }
}
