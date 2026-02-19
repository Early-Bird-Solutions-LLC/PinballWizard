using PinballWizard.Processor;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ProcessorSettings>(
    builder.Configuration.GetSection("Processor"));

var app = builder.Build();

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", service = "processor" }));

// Event Grid webhook validation + blob event handler will be added by processor-agent
app.MapPost("/api/events", () => Results.Ok());

app.Run();
