using System.Text.Json;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Options;
using PinballWizard.Api.Pipeline;
using PinballWizard.Domain.Models;

namespace PinballWizard.Api.Services;

public interface IConversationStore
{
    Task SaveMessageAsync(string conversationId, string userId, string role, string content, CancellationToken ct = default);
    Task<List<ConversationTurn>> GetHistoryAsync(string conversationId, CancellationToken ct = default);
    Task SaveResponseAsync(string conversationId, string userId, ChatResponse response, CancellationToken ct = default);
    Task<List<ConversationSummary>> GetConversationsAsync(string userId, CancellationToken ct = default);
}

public sealed class ConversationStore(TableClient tableClient) : IConversationStore
{
    public async Task SaveMessageAsync(string conversationId, string userId, string role, string content, CancellationToken ct = default)
    {
        var entity = new TableEntity(conversationId, $"{DateTimeOffset.UtcNow.Ticks:D20}")
        {
            ["UserId"] = userId,
            ["Role"] = role,
            ["Content"] = content,
            ["Timestamp"] = DateTimeOffset.UtcNow
        };

        await tableClient.UpsertEntityAsync(entity, cancellationToken: ct);
    }

    public async Task<List<ConversationTurn>> GetHistoryAsync(string conversationId, CancellationToken ct = default)
    {
        var turns = new List<ConversationTurn>();
        var query = tableClient.QueryAsync<TableEntity>(
            filter: $"PartitionKey eq '{conversationId}'",
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            turns.Add(new ConversationTurn
            {
                Role = entity.GetString("Role"),
                Content = entity.GetString("Content")
            });
        }

        return turns;
    }

    public async Task SaveResponseAsync(string conversationId, string userId, ChatResponse response, CancellationToken ct = default)
    {
        await SaveMessageAsync(conversationId, userId, "assistant", response.Answer, ct);
    }

    public async Task<List<ConversationSummary>> GetConversationsAsync(string userId, CancellationToken ct = default)
    {
        var conversations = new Dictionary<string, ConversationSummary>();
        var query = tableClient.QueryAsync<TableEntity>(
            filter: $"UserId eq '{userId}'",
            cancellationToken: ct);

        await foreach (var entity in query)
        {
            var convId = entity.PartitionKey!;
            if (!conversations.ContainsKey(convId))
            {
                var content = entity.GetString("Content") ?? "";
                conversations[convId] = new ConversationSummary
                {
                    ConversationId = convId,
                    Title = content.Length > 80 ? content[..80] + "..." : content,
                    CreatedAt = entity.Timestamp ?? DateTimeOffset.UtcNow,
                    LastMessageAt = entity.Timestamp ?? DateTimeOffset.UtcNow,
                    MessageCount = 1
                };
            }
            else
            {
                var conv = conversations[convId];
                conversations[convId] = new ConversationSummary
                {
                    ConversationId = conv.ConversationId,
                    Title = conv.Title,
                    CreatedAt = conv.CreatedAt,
                    LastMessageAt = entity.Timestamp ?? conv.LastMessageAt,
                    MessageCount = conv.MessageCount + 1
                };
            }
        }

        return conversations.Values
            .OrderByDescending(c => c.LastMessageAt)
            .ToList();
    }
}
