using System.Runtime.CompilerServices;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using PinballWizard.Api.Pipeline;
using PinballWizard.Api.Services;
using PinballWizard.Domain.Models;

namespace PinballWizard.Api.Hubs;

[Authorize]
public sealed class ChatHub(
    IQueryPreprocessor preprocessor,
    ISearchService searchService,
    IContextAssembler contextAssembler,
    IPromptBuilder promptBuilder,
    IChatService chatService,
    IResponseFormatter responseFormatter,
    IConversationStore conversationStore) : Hub
{
    public async IAsyncEnumerable<ChatStreamEvent> SendMessage(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        var conversationId = request.ConversationId ?? Guid.NewGuid().ToString("N");

        // Save user message
        await conversationStore.SaveMessageAsync(conversationId, userId, "user", request.Message, ct);

        // Run pipeline
        var preprocessed = preprocessor.Process(request.Message, request.GameFilter);
        var searchResults = await searchService.SearchAsync(preprocessed, ct);
        var context = contextAssembler.Assemble(searchResults);
        var history = await conversationStore.GetHistoryAsync(conversationId, ct);
        var messages = promptBuilder.Build(context, request.Message, history);

        // Stream events to caller
        var fullText = new System.Text.StringBuilder();
        await foreach (var evt in chatService.StreamAsync(messages, context.Blocks, ct))
        {
            if (evt.Type == ChatStreamEventType.TextDelta && evt.Text is not null)
                fullText.Append(evt.Text);

            yield return evt;
        }

        // Save the complete response
        var response = responseFormatter.Format(conversationId, fullText.ToString(), context.Blocks);
        await conversationStore.SaveResponseAsync(conversationId, userId, response, ct);
    }
}
