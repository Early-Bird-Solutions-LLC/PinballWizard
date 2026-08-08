using System.Runtime.CompilerServices;
using PinballWizard.Domain.Models;

namespace PinballWizard.Web.Services;

public sealed class MockChatService : IChatService
{
    private static readonly Dictionary<string, string> CannedResponses = new()
    {
        ["flipper"] = "**Stuck flippers** are one of the most common pinball issues. Here's how to diagnose and fix them:\n\n1. **Check the EOS (End of Stroke) switch** — If it's not adjusted correctly, the flipper coil stays energized and can overheat or stick.\n2. **Inspect the flipper coil** — Look for signs of burning or melting. A shorted coil will need replacement.\n3. **Check the flipper bushing** — Worn bushings cause friction. Replace with new nylon bushings.\n4. **Examine the flipper link and pawl** — These mechanical parts wear over time.\n5. **Test the driver board transistor** — A shorted transistor can keep the coil energized.\n\nFor Stern machines, the service manual has a detailed flipper assembly diagram in the Maintenance section.",
        ["rarest"] = "The **rarest pinball machines** are highly sought after by collectors. Here are some of the most rare:\n\n1. **Pinball Circus** (1994, Bally) — Only 2 prototypes were made. One sold for over $100,000.\n2. **Kingpin** (1996, Capcom) — Only about 500 units produced before Capcom exited pinball.\n3. **The Mafia** (2001, Unidesa) — Spanish-made, extremely limited distribution.\n4. **Varkon** (1982, Williams) — Only 2 prototypes exist.\n5. **Granny and the Gators** (1984, Bally) — Approximately 300 units produced.\n\nProduction numbers under 1,000 units generally make a machine \"rare\" in collector circles.",
        ["medieval madness"] = "**Medieval Madness** (1997, Williams) is widely considered one of the greatest pinball machines ever made.\n\n## Rules Overview\n\nThe main objective is to **destroy all 6 castles** to reach the wizard mode \"Battle for the Kingdom.\"\n\n### Castle Destruction\n- Hit the castle to lower the drawbridge\n- Make shots to damage the castle\n- Each successive castle requires more hits\n\n### Key Shots\n- **Left ramp** — Peasant Catapult\n- **Right ramp** — Joust\n- **Center shot** — Castle attack\n- **Troll targets** — Light Troll Madness multiball\n\n### Multiball Modes\n- **Castle Multiball** — Destroy castle during multiball for huge points\n- **Troll Madness** — Hit troll targets, then lock balls\n- **Royal Madness** — Available after destroying multiple castles"
    };

    public Task<ChatResponse> SendMessageAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var answer = GetCannedResponse(request.Message);
        return Task.FromResult(new ChatResponse
        {
            ConversationId = request.ConversationId ?? Guid.NewGuid().ToString("N"),
            Answer = answer,
            Sources =
            [
                new SourceCitation { Index = 1, Title = "Pinball Repair Guide", Url = "https://example.com/repair", DocumentType = "RepairGuide", Score = 0.95 },
                new SourceCitation { Index = 2, Title = "Game Manual", Url = "https://example.com/manual", DocumentType = "Manual", GameTitle = "Medieval Madness", Score = 0.88 }
            ]
        });
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamMessageAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatStreamEvent
        {
            Type = ChatStreamEventType.Sources,
            Sources =
            [
                new SourceCitation { Index = 1, Title = "Pinball Repair Guide", Url = "https://example.com/repair", DocumentType = "RepairGuide", Score = 0.95 },
                new SourceCitation { Index = 2, Title = "Game Manual", Url = "https://example.com/manual", DocumentType = "Manual", GameTitle = "Medieval Madness", Score = 0.88 }
            ]
        };

        var answer = GetCannedResponse(request.Message);
        var words = answer.Split(' ');

        foreach (var word in words)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatStreamEvent
            {
                Type = ChatStreamEventType.TextDelta,
                Text = word + " "
            };
            await Task.Delay(50, cancellationToken);
        }

        yield return new ChatStreamEvent { Type = ChatStreamEventType.Complete };
    }

    public Task<List<ConversationSummary>> GetConversationsAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<ConversationSummary>
        {
            new() { ConversationId = "conv-1", Title = "Flipper repair help", CreatedAt = DateTimeOffset.UtcNow.AddHours(-2), LastMessageAt = DateTimeOffset.UtcNow.AddHours(-1), MessageCount = 4 },
            new() { ConversationId = "conv-2", Title = "Medieval Madness rules", CreatedAt = DateTimeOffset.UtcNow.AddDays(-1), LastMessageAt = DateTimeOffset.UtcNow.AddDays(-1), MessageCount = 6 }
        });
    }

    public Task<List<ChatResponse>> GetConversationHistoryAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new List<ChatResponse>());
    }

    public Task SubmitFeedbackAsync(FeedbackRequest request, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    private static string GetCannedResponse(string message)
    {
        var lower = message.ToLowerInvariant();
        foreach (var (key, value) in CannedResponses)
        {
            if (lower.Contains(key))
                return value;
        }

        return $"I'd be happy to help with your pinball question about \"{message}\". As a pinball knowledge assistant, I can help with game rules, repair guides, parts identification, and general pinball history. Could you provide more details about what you'd like to know?";
    }
}
