using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using PinballWizard.Domain.Models;
using PinballWizard.Web.Components.Games;
using Xunit;

namespace PinballWizard.Web.Tests;

public class GameCardTests : BunitContext, IAsyncLifetime
{
    public GameCardTests()
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
    public void GameCard_DisplaysGameTitle()
    {
        var game = CreateTestGame();
        var cut = Render<GameCard>(parameters => parameters.Add(p => p.Game, game));

        Assert.Contains("Medieval Madness", cut.Markup);
    }

    [Fact]
    public void GameCard_DisplaysManufacturerAndYear()
    {
        var game = CreateTestGame();
        var cut = Render<GameCard>(parameters => parameters.Add(p => p.Game, game));

        Assert.Contains("Williams", cut.Markup);
        Assert.Contains("1997", cut.Markup);
    }

    [Fact]
    public void GameCard_DisplaysMachineType()
    {
        var game = CreateTestGame();
        var cut = Render<GameCard>(parameters => parameters.Add(p => p.Game, game));

        Assert.Contains("DMD", cut.Markup);
    }

    [Fact]
    public void GameCard_DisplaysDocumentCount()
    {
        var game = CreateTestGame();
        var cut = Render<GameCard>(parameters => parameters.Add(p => p.Game, game));

        Assert.Contains("12 documents", cut.Markup);
    }

    [Fact]
    public void GameCard_DisplaysEditions()
    {
        var game = CreateTestGame();
        var cut = Render<GameCard>(parameters => parameters.Add(p => p.Game, game));

        Assert.Contains("Standard", cut.Markup);
        Assert.Contains("Royal Edition", cut.Markup);
    }

    private static GameSummary CreateTestGame() => new()
    {
        GameId = "game_medieval-madness",
        Title = "Medieval Madness",
        Slug = "medieval-madness",
        Manufacturer = "Williams",
        Year = 1997,
        MachineType = "DMD",
        DocumentCount = 12,
        Editions =
        [
            new EditionInfo { Name = "Standard" },
            new EditionInfo { Name = "Royal Edition" }
        ]
    };
}
