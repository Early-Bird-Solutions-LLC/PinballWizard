using Bunit;
using MudBlazor.Services;
using PinballWizard.Domain.Models;
using PinballWizard.Web.Components.Chat;
using Xunit;

namespace PinballWizard.Web.Tests;

public class MessageBubbleTests : BunitContext, IAsyncLifetime
{
    public MessageBubbleTests()
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
    public void UserMessage_HasUserStyling()
    {
        var cut = Render<MessageBubble>(parameters => parameters
            .Add(p => p.Content, "Hello, help me with pinball!")
            .Add(p => p.IsUser, true));

        Assert.Contains("message-user", cut.Markup);
        Assert.Contains("Hello, help me with pinball!", cut.Markup);
    }

    [Fact]
    public void AssistantMessage_HasAssistantStyling()
    {
        var cut = Render<MessageBubble>(parameters => parameters
            .Add(p => p.Content, "Sure, I can help with that.")
            .Add(p => p.IsUser, false));

        Assert.Contains("message-assistant", cut.Markup);
    }

    [Fact]
    public void AssistantMessage_RendersMarkdown()
    {
        var cut = Render<MessageBubble>(parameters => parameters
            .Add(p => p.Content, "**Bold text** and *italic text*")
            .Add(p => p.IsUser, false));

        Assert.Contains("<strong>Bold text</strong>", cut.Markup);
        Assert.Contains("<em>italic text</em>", cut.Markup);
    }

    [Fact]
    public void AssistantMessage_ShowsSources()
    {
        var sources = new List<SourceCitation>
        {
            new() { Index = 1, Title = "Repair Guide", Url = "https://example.com/repair", Score = 0.95 },
            new() { Index = 2, Title = "Game Manual", Url = "https://example.com/manual", Score = 0.88 }
        };

        var cut = Render<MessageBubble>(parameters => parameters
            .Add(p => p.Content, "Here is some info.")
            .Add(p => p.IsUser, false)
            .Add(p => p.Sources, sources));

        Assert.Contains("2 sources", cut.Markup);
    }

    [Fact]
    public void UserMessage_DoesNotRenderMarkdown()
    {
        var cut = Render<MessageBubble>(parameters => parameters
            .Add(p => p.Content, "**Bold text**")
            .Add(p => p.IsUser, true));

        Assert.DoesNotContain("<strong>", cut.Markup);
        Assert.Contains("**Bold text**", cut.Markup);
    }
}
