using System.Security.Claims;
using System.Threading.RateLimiting;
using PinballWizard.Api.Pipeline;
using PinballWizard.Api.Services;
using PinballWizard.Domain.Models;

namespace PinballWizard.Api.Endpoints;

public static class ChatEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/chat", HandleChat)
            .RequireAuthorization()
            .RequireRateLimiting("chat");

        group.MapGet("/chat/{id}/history", HandleGetHistory)
            .RequireAuthorization()
            .RequireRateLimiting("general");
    }

    private static async Task<IResult> HandleChat(
        ChatRequest request,
        IQueryPreprocessor preprocessor,
        ISearchService searchService,
        IContextAssembler contextAssembler,
        IPromptBuilder promptBuilder,
        IChatService chatService,
        IResponseFormatter responseFormatter,
        IConversationStore conversationStore,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        var conversationId = request.ConversationId ?? Guid.NewGuid().ToString("N");

        // Save user message
        await conversationStore.SaveMessageAsync(conversationId, userId, "user", request.Message, ct);

        // Run pipeline
        var preprocessed = preprocessor.Process(request.Message, request.GameFilter);
        var searchResults = await searchService.SearchAsync(preprocessed, ct);
        var context = contextAssembler.Assemble(searchResults);
        var history = await conversationStore.GetHistoryAsync(conversationId, ct);
        var messages = promptBuilder.Build(context, request.Message, history);

        // Collect full response from stream
        var fullText = new System.Text.StringBuilder();
        await foreach (var evt in chatService.StreamAsync(messages, context.Blocks, ct))
        {
            if (evt.Type == ChatStreamEventType.TextDelta && evt.Text is not null)
                fullText.Append(evt.Text);
        }

        var response = responseFormatter.Format(conversationId, fullText.ToString(), context.Blocks);

        // Save assistant response
        await conversationStore.SaveResponseAsync(conversationId, userId, response, ct);

        return Results.Ok(response);
    }

    private static async Task<IResult> HandleGetHistory(
        string id,
        IConversationStore conversationStore,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var history = await conversationStore.GetHistoryAsync(id, ct);
        return Results.Ok(history);
    }
}
