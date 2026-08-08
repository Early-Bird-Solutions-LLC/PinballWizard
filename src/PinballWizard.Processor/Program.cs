using Azure.AI.DocumentIntelligence;
using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using PinballWizard.Domain.Abstractions;
using PinballWizard.Processor;
using PinballWizard.Processor.Chunking;
using PinballWizard.Processor.Indexing;
using PinballWizard.Processor.Pipeline;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ProcessorSettings>(
    builder.Configuration.GetSection("Processor"));

var credential = new DefaultAzureCredential();

// Azure service clients
builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<IOptions<ProcessorSettings>>().Value;
    return new BlobServiceClient(settings.StorageConnectionString);
});

builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<IOptions<ProcessorSettings>>().Value;
    return new DocumentIntelligenceClient(new Uri(settings.DocumentIntelligenceEndpoint), credential);
});

builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<IOptions<ProcessorSettings>>().Value;
    return new SearchIndexClient(new Uri(settings.SearchEndpoint), credential);
});

builder.Services.AddSingleton(sp =>
{
    var settings = sp.GetRequiredService<IOptions<ProcessorSettings>>().Value;
    var indexClient = sp.GetRequiredService<SearchIndexClient>();
    return indexClient.GetSearchClient(settings.SearchIndexName);
});

// Content extractors
builder.Services.AddSingleton<IContentExtractor, PdfExtractor>();
builder.Services.AddSingleton<IContentExtractor, HtmlExtractor>();
builder.Services.AddSingleton<IContentExtractor, JsonExtractor>();
builder.Services.AddSingleton<IContentExtractor, VideoTranscriber>();
builder.Services.AddSingleton<IContentExtractor, ImageExtractor>();

// Chunking strategies
builder.Services.AddSingleton<SlidingWindowChunker>();
builder.Services.AddSingleton<SectionAwareChunker>();
builder.Services.AddSingleton<WholeDocumentChunker>();

// Indexing
builder.Services.AddSingleton<SearchIndexManager>();
builder.Services.AddSingleton<IndexBatchPublisher>();

// Pipeline
builder.Services.AddSingleton<PipelineOrchestrator>();
builder.Services.AddSingleton<EventGridHandler>();

var app = builder.Build();

app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", service = "processor" }));

app.MapPost("/api/events", (HttpRequest request, EventGridHandler handler, CancellationToken ct) =>
    handler.HandleAsync(request, ct));

await app.RunAsync();
