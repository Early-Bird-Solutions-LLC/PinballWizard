using Azure.Data.Tables;
using PinballWizard.Domain.Models;

namespace PinballWizard.Api.Services;

public interface IFeedbackService
{
    Task SubmitFeedbackAsync(string userId, FeedbackRequest feedback, CancellationToken ct = default);
}

public sealed class FeedbackService(TableClient tableClient) : IFeedbackService
{
    public async Task SubmitFeedbackAsync(string userId, FeedbackRequest feedback, CancellationToken ct = default)
    {
        var entity = new TableEntity(feedback.ConversationId, feedback.MessageId)
        {
            ["UserId"] = userId,
            ["IsHelpful"] = feedback.IsHelpful,
            ["Comment"] = feedback.Comment ?? "",
            ["SubmittedAt"] = DateTimeOffset.UtcNow
        };

        await tableClient.UpsertEntityAsync(entity, cancellationToken: ct);
    }
}
