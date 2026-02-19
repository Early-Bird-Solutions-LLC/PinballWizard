namespace PinballWizard.Domain.Models;

/// <summary>
/// Shared chat/RAG types used by Api (produce) and Web (consume).
/// </summary>

public sealed class ChatRequest
{
    public required string Message { get; init; }
    public string? ConversationId { get; init; }
    public string? GameFilter { get; init; }
}

public sealed class ChatResponse
{
    public required string ConversationId { get; init; }
    public required string Answer { get; init; }
    public List<SourceCitation> Sources { get; init; } = [];
}

public sealed class SourceCitation
{
    public required int Index { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? DocumentType { get; init; }
    public string? GameTitle { get; init; }
    public string? SectionPath { get; init; }
    public double Score { get; init; }
}

public enum ChatStreamEventType
{
    Sources,
    TextDelta,
    Complete,
    Error
}

public sealed class ChatStreamEvent
{
    public required ChatStreamEventType Type { get; init; }
    public string? Text { get; init; }
    public List<SourceCitation>? Sources { get; init; }
    public string? Error { get; init; }
}

public sealed class SearchRequest
{
    public required string Query { get; init; }
    public string? GameFilter { get; init; }
    public string? DocumentTypeFilter { get; init; }
    public int Top { get; init; } = 10;
}

public sealed class SearchResult
{
    public required string ChunkId { get; init; }
    public required string Content { get; init; }
    public required double Score { get; init; }
    public string? GameTitle { get; init; }
    public string? DocumentType { get; init; }
    public string? SourceUrl { get; init; }
    public string? SectionPath { get; init; }
}

public sealed class GameSummary
{
    public required string GameId { get; init; }
    public required string Title { get; init; }
    public required string Slug { get; init; }
    public string? Manufacturer { get; init; }
    public int? Year { get; init; }
    public string? MachineType { get; init; }
    public int DocumentCount { get; init; }
    public List<EditionInfo> Editions { get; init; } = [];
}

public sealed class FeedbackRequest
{
    public required string ConversationId { get; init; }
    public required string MessageId { get; init; }
    public required bool IsHelpful { get; init; }
    public string? Comment { get; init; }
}

public sealed class ConversationSummary
{
    public required string ConversationId { get; init; }
    public required string Title { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastMessageAt { get; init; }
    public int MessageCount { get; init; }
}

public sealed class UserInfo
{
    public required string UserId { get; init; }
    public required string DisplayName { get; init; }
    public string? Email { get; init; }
    public required string Provider { get; init; }
    public string? AvatarUrl { get; init; }
}
