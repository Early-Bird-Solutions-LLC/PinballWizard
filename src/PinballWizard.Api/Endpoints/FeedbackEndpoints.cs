using System.Security.Claims;
using PinballWizard.Api.Services;
using PinballWizard.Domain.Models;

namespace PinballWizard.Api.Endpoints;

public static class FeedbackEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapPost("/feedback", HandleFeedback)
            .RequireAuthorization()
            .RequireRateLimiting("general");
    }

    private static async Task<IResult> HandleFeedback(
        FeedbackRequest request,
        IFeedbackService feedbackService,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
        await feedbackService.SubmitFeedbackAsync(userId, request, ct);
        return Results.Ok(new { status = "received" });
    }
}
