using PinballWizard.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ApiSettings>(
    builder.Configuration.GetSection("Api"));

var app = builder.Build();

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", service = "api" }));

app.Run();
