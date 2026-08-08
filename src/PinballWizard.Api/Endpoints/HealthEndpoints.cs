namespace PinballWizard.Api.Endpoints;

public static class HealthEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "api" }));
    }
}
