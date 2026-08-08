using Microsoft.Extensions.Options;
using PinballWizard.Api.Pipeline;
using Xunit;

namespace PinballWizard.Api.Tests;

public class PromptBuilderTests
{
    private static PromptBuilder CreateBuilder(int maxTurns = 10)
    {
        var settings = Options.Create(new ApiSettings { MaxConversationTurns = maxTurns });
        return new PromptBuilder(settings);
    }

    [Fact]
    public void Build_IncludesSystemPromptWithContext()
    {
        var builder = CreateBuilder();
        var context = new ContextResult
        {
            FormattedContext = "[1] (source: Test)\nSome content",
            Blocks = [],
            TotalTokens = 10
        };

        var messages = builder.Build(context, "What is pinball?", null);

        Assert.Equal("system", messages[0].Role);
        Assert.Contains("PinballWizard", messages[0].Content);
        Assert.Contains("[1] (source: Test)", messages[0].Content);
        Assert.Contains("Some content", messages[0].Content);
    }

    [Fact]
    public void Build_IncludesUserQuestion()
    {
        var builder = CreateBuilder();
        var context = new ContextResult { FormattedContext = "", Blocks = [], TotalTokens = 0 };

        var messages = builder.Build(context, "My question here", null);

        var lastMessage = messages.Last();
        Assert.Equal("user", lastMessage.Role);
        Assert.Equal("My question here", lastMessage.Content);
    }

    [Fact]
    public void Build_IncludesConversationHistory()
    {
        var builder = CreateBuilder();
        var context = new ContextResult { FormattedContext = "", Blocks = [], TotalTokens = 0 };
        var history = new List<ConversationTurn>
        {
            new() { Role = "user", Content = "Previous question" },
            new() { Role = "assistant", Content = "Previous answer" }
        };

        var messages = builder.Build(context, "Follow-up question", history);

        // system + 2 history + user question = 4
        Assert.Equal(4, messages.Count);
        Assert.Equal("user", messages[1].Role);
        Assert.Equal("Previous question", messages[1].Content);
        Assert.Equal("assistant", messages[2].Role);
        Assert.Equal("Previous answer", messages[2].Content);
    }

    [Fact]
    public void Build_LimitsConversationHistoryToMaxTurns()
    {
        var builder = CreateBuilder(maxTurns: 2);
        var context = new ContextResult { FormattedContext = "", Blocks = [], TotalTokens = 0 };
        var history = new List<ConversationTurn>
        {
            new() { Role = "user", Content = "Question 1" },
            new() { Role = "assistant", Content = "Answer 1" },
            new() { Role = "user", Content = "Question 2" },
            new() { Role = "assistant", Content = "Answer 2" },
            new() { Role = "user", Content = "Question 3" },
            new() { Role = "assistant", Content = "Answer 3" }
        };

        var messages = builder.Build(context, "Question 4", history);

        // system + last 4 history messages (2 turns * 2) + user = 6
        Assert.Equal(6, messages.Count);
        // The oldest messages should be trimmed
        Assert.DoesNotContain(messages, m => m.Content == "Question 1");
    }

    [Fact]
    public void Build_NoHistory_OnlySystemAndUser()
    {
        var builder = CreateBuilder();
        var context = new ContextResult { FormattedContext = "", Blocks = [], TotalTokens = 0 };

        var messages = builder.Build(context, "Single question", null);

        Assert.Equal(2, messages.Count);
        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[1].Role);
    }
}
