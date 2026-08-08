using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace PinballWizard.Processor.Pipeline;

public sealed class EventGridHandler
{
    private readonly PipelineOrchestrator _orchestrator;
    private readonly ILogger<EventGridHandler> _logger;

    public EventGridHandler(PipelineOrchestrator orchestrator, ILogger<EventGridHandler> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    public async Task<IResult> HandleAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync(ct);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var events = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToList()
            : [root];

        foreach (var cloudEvent in events)
        {
            var validationResult = TryHandleValidation(cloudEvent);
            if (validationResult is not null)
                return validationResult;

            await TryHandleBlobEvent(cloudEvent, ct);
        }

        return Results.Ok();
    }

    private IResult? TryHandleValidation(JsonElement cloudEvent)
    {
        if (!cloudEvent.TryGetProperty("type", out var eventType))
            return null;

        if (eventType.GetString() != "Microsoft.EventGrid.SubscriptionValidationEvent")
            return null;

        if (!cloudEvent.TryGetProperty("data", out var data)
            || !data.TryGetProperty("validationCode", out var validationCode))
            return null;

        _logger.LogInformation("Event Grid subscription validation request received");
        return Results.Ok(new { validationResponse = validationCode.GetString() });
    }

    private async Task TryHandleBlobEvent(JsonElement cloudEvent, CancellationToken ct)
    {
        var type = cloudEvent.TryGetProperty("type", out var t) ? t.GetString() : null;

        if (type is not ("Microsoft.Storage.BlobCreated" or "Microsoft.Storage.BlobUpdated"))
            return;

        var (containerName, blobName) = ParseBlobSubject(cloudEvent);
        if (containerName is null || blobName is null)
        {
            _logger.LogWarning("Could not parse container/blob from event subject");
            return;
        }

        _logger.LogInformation("Processing blob event: {Container}/{Blob}", containerName, blobName);

        try
        {
            await _orchestrator.ProcessBlobAsync(containerName, blobName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing blob {Container}/{Blob}", containerName, blobName);
        }
    }

    private static (string? Container, string? Blob) ParseBlobSubject(JsonElement cloudEvent)
    {
        if (!cloudEvent.TryGetProperty("subject", out var subject))
            return (null, null);

        var subjectStr = subject.GetString();
        if (subjectStr is null)
            return (null, null);

        var containerIdx = subjectStr.IndexOf("/containers/", StringComparison.Ordinal);
        var blobIdx = subjectStr.IndexOf("/blobs/", StringComparison.Ordinal);

        if (containerIdx < 0 || blobIdx < 0)
            return (null, null);

        var containerName = subjectStr[(containerIdx + "/containers/".Length)..blobIdx];
        var blobName = subjectStr[(blobIdx + "/blobs/".Length)..];
        return (containerName, blobName);
    }
}
