using PinballWizard.Domain.Models;

namespace PinballWizard.Web.Services;

public interface IChatService
{
    Task<ChatResponse> SendMessageAsync(ChatRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ChatStreamEvent> StreamMessageAsync(ChatRequest request, CancellationToken cancellationToken = default);
    Task<List<ConversationSummary>> GetConversationsAsync(CancellationToken cancellationToken = default);
    Task<List<ChatResponse>> GetConversationHistoryAsync(string conversationId, CancellationToken cancellationToken = default);
    Task SubmitFeedbackAsync(FeedbackRequest request, CancellationToken cancellationToken = default);
}
