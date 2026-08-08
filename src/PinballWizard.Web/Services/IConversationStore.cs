using PinballWizard.Domain.Models;

namespace PinballWizard.Web.Services;

public interface IConversationStore
{
    Task<string> CreateConversationAsync(string title, CancellationToken cancellationToken = default);
    Task AddMessageAsync(string conversationId, string role, string content, List<SourceCitation>? sources = null, CancellationToken cancellationToken = default);
    Task<List<StoredMessage>> GetMessagesAsync(string conversationId, CancellationToken cancellationToken = default);
    Task<List<ConversationSummary>> ListConversationsAsync(CancellationToken cancellationToken = default);
}

public sealed class StoredMessage
{
    public required string Role { get; init; }
    public required string Content { get; init; }
    public List<SourceCitation> Sources { get; init; } = [];
    public DateTimeOffset Timestamp { get; init; }
}
