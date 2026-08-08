using System.Collections.Concurrent;
using PinballWizard.Domain.Models;

namespace PinballWizard.Web.Services;

public sealed class MockConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, ConversationData> _conversations = new();

    public Task<string> CreateConversationAsync(string title, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid().ToString("N");
        _conversations[id] = new ConversationData
        {
            Title = title,
            CreatedAt = DateTimeOffset.UtcNow
        };
        return Task.FromResult(id);
    }

    public Task AddMessageAsync(string conversationId, string role, string content, List<SourceCitation>? sources = null, CancellationToken cancellationToken = default)
    {
        if (!_conversations.TryGetValue(conversationId, out var conversation))
        {
            conversation = new ConversationData
            {
                Title = content.Length > 50 ? content[..50] + "..." : content,
                CreatedAt = DateTimeOffset.UtcNow
            };
            _conversations[conversationId] = conversation;
        }

        conversation.Messages.Add(new StoredMessage
        {
            Role = role,
            Content = content,
            Sources = sources ?? [],
            Timestamp = DateTimeOffset.UtcNow
        });

        return Task.CompletedTask;
    }

    public Task<List<StoredMessage>> GetMessagesAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (_conversations.TryGetValue(conversationId, out var conversation))
            return Task.FromResult(conversation.Messages.ToList());

        return Task.FromResult(new List<StoredMessage>());
    }

    public Task<List<ConversationSummary>> ListConversationsAsync(CancellationToken cancellationToken = default)
    {
        var summaries = _conversations.Select(kvp => new ConversationSummary
        {
            ConversationId = kvp.Key,
            Title = kvp.Value.Title,
            CreatedAt = kvp.Value.CreatedAt,
            LastMessageAt = kvp.Value.Messages.Count > 0
                ? kvp.Value.Messages[^1].Timestamp
                : kvp.Value.CreatedAt,
            MessageCount = kvp.Value.Messages.Count
        })
        .OrderByDescending(c => c.LastMessageAt)
        .ToList();

        return Task.FromResult(summaries);
    }

    private sealed class ConversationData
    {
        public required string Title { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public List<StoredMessage> Messages { get; } = [];
    }
}
