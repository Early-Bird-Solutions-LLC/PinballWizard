using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using PinballWizard.Domain.Models;
using PinballWizard.Web.Components.Chat;
using PinballWizard.Web.Services;
using Xunit;

namespace PinballWizard.Web.Tests;

public class ChatContainerTests : BunitContext, IAsyncLifetime
{
    public ChatContainerTests()
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
    public void ChatContainer_RendersInputField()
    {
        RegisterMockServices();

        var cut = Render<ChatContainer>();

        Assert.Contains("Ask a pinball question", cut.Markup);
    }

    [Fact]
    public void ChatContainer_RendersSendButton()
    {
        RegisterMockServices();

        var cut = Render<ChatContainer>();

        Assert.Contains("Send message", cut.Markup);
    }

    [Fact]
    public void ChatContainer_RendersGameSelector()
    {
        RegisterMockServices();

        var cut = Render<ChatContainer>();

        Assert.Contains("Filter by game", cut.Markup);
    }

    private void RegisterMockServices()
    {
        var chatService = Substitute.For<IChatService>();
        chatService.StreamMessageAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(AsyncEnumerable.Empty<ChatStreamEvent>());

        var gameCatalog = Substitute.For<IGameCatalogService>();
        gameCatalog.SearchGamesAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<GameSummary>());

        var conversationStore = Substitute.For<IConversationStore>();

        Services.AddSingleton(chatService);
        Services.AddSingleton(gameCatalog);
        Services.AddSingleton(conversationStore);
    }
}
