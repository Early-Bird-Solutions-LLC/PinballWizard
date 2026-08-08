using Microsoft.Extensions.Options;

namespace PinballWizard.Api.Pipeline;

public interface IPromptBuilder
{
    List<PromptMessage> Build(ContextResult context, string userQuestion, List<ConversationTurn>? history);
}

public sealed record PromptMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
}

public sealed record ConversationTurn
{
    public required string Role { get; init; }
    public required string Content { get; init; }
}

public sealed class PromptBuilder(IOptions<ApiSettings> settings) : IPromptBuilder
{
    private const string SystemPromptTemplate = """
        You are PinballWizard, an expert pinball knowledge assistant. You help players,
        collectors, technicians, and enthusiasts with anything pinball-related.

        RULES:
        1. Answer ONLY from the provided context. If the context doesn't contain the
           answer, say "I don't have enough information about that in my knowledge base."
        2. Cite your sources using [1], [2], etc. matching the numbered context blocks.
        3. Every factual claim MUST have at least one citation.
        4. For repair/maintenance questions, include safety warnings where appropriate.
        5. If the question is about a specific game, focus on that game's documentation.
        6. For general questions, draw from multiple sources to give comprehensive answers.
        7. Use clear, concise language. Use markdown formatting for readability.
        8. If the question is ambiguous (e.g., "Flash" could be multiple games), ask
           for clarification.

        CONTEXT:
        {context}
        """;

    public List<PromptMessage> Build(ContextResult context, string userQuestion, List<ConversationTurn>? history)
    {
        var messages = new List<PromptMessage>();

        // System prompt with context
        var systemContent = SystemPromptTemplate.Replace("{context}", context.FormattedContext);
        messages.Add(new PromptMessage { Role = "system", Content = systemContent });

        // Conversation history (last N turns)
        if (history is { Count: > 0 })
        {
            var maxTurns = settings.Value.MaxConversationTurns;
            var recentHistory = history.TakeLast(maxTurns * 2); // Each turn is user+assistant
            foreach (var turn in recentHistory)
            {
                messages.Add(new PromptMessage { Role = turn.Role, Content = turn.Content });
            }
        }

        // Current user question
        messages.Add(new PromptMessage { Role = "user", Content = userQuestion });

        return messages;
    }
}
