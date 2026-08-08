using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Domain.Models;
using PinballWizard.Web.Services;
using Xunit;
using Index = PinballWizard.Web.Pages.Index;

namespace PinballWizard.Web.Tests;

public class WebSmokeTests : BunitContext, IAsyncLifetime
{
    public WebSmokeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options =>
        {
            options.PopoverOptions.CheckForPopoverProvider = false;
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
    }

    [Fact]
    public void IndexPage_Renders_BrandTitle()
    {
        RegisterMockServices();

        var cut = Render<Index>();

        Assert.Contains("PinWiz.ai", cut.Markup);
    }

    [Fact]
    public void IndexPage_Renders_HeroSection()
    {
        RegisterMockServices();

        var cut = Render<Index>();

        Assert.Contains("AI-powered pinball knowledge base", cut.Markup);
    }

    [Fact]
    public void IndexPage_Renders_FeaturedQuestions()
    {
        RegisterMockServices();

        var cut = Render<Index>();

        Assert.Contains("Popular Questions", cut.Markup);
        Assert.Contains("How do I fix a stuck flipper?", cut.Markup);
    }

    [Fact]
    public void IndexPage_Renders_StatsBar()
    {
        RegisterMockServices();

        var cut = Render<Index>();

        Assert.Contains("Documents Indexed", cut.Markup);
        Assert.Contains("Games Cataloged", cut.Markup);
        Assert.Contains("Questions Answered", cut.Markup);
    }

    private void RegisterMockServices()
    {
        var gameCatalog = Substitute.For<IGameCatalogService>();
        gameCatalog.GetStatsAsync(Arg.Any<CancellationToken>())
            .Returns(new GameCatalogStats { TotalGames = 12, TotalDocuments = 115, TotalQuestionsAnswered = 1247 });
        gameCatalog.SearchGamesAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<GameSummary>());
        gameCatalog.GetManufacturersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        var chatService = Substitute.For<IChatService>();
        var conversationStore = Substitute.For<IConversationStore>();

        Services.AddSingleton(gameCatalog);
        Services.AddSingleton(chatService);
        Services.AddSingleton(conversationStore);
    }
}
