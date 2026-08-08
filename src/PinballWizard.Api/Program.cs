using System.Text;
using System.Threading.RateLimiting;
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using PinballWizard.Api;
using PinballWizard.Api.Auth;
using PinballWizard.Api.Endpoints;
using PinballWizard.Api.Hubs;
using PinballWizard.Api.Pipeline;
using PinballWizard.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Settings
builder.Services.Configure<ApiSettings>(
    builder.Configuration.GetSection("Api"));

var settings = builder.Configuration.GetSection("Api").Get<ApiSettings>() ?? new ApiSettings();

// Authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = "PinballWizard",
        ValidAudience = "PinballWizard",
        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(settings.JwtSigningKey.Length >= 32
                ? settings.JwtSigningKey
                : settings.JwtSigningKey.PadRight(32, '0')))
    };

    // Allow SignalR to receive token from query string
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs/chat"))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
})
.AddGoogle(options =>
{
    options.ClientId = builder.Configuration["Authentication:Google:ClientId"] ?? "";
    options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? "";
});

builder.Services.AddAuthorization();

// Rate limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("chat", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User?.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromHours(1)
            }));

    options.AddPolicy("general", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1)
            }));

    options.AddPolicy("search", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1)
            }));
});

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5000"])
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// SignalR
builder.Services.AddSignalR();

// HttpClient for Foundry
builder.Services.AddHttpClient("Foundry");

// Azure services
var credential = new DefaultAzureCredential();

builder.Services.AddSingleton(_ =>
    new SearchClient(
        new Uri(settings.SearchEndpoint),
        settings.SearchIndexName,
        credential));

builder.Services.AddSingleton<TableClient>(_ =>
{
    var storageEndpoint = builder.Configuration["Storage:TableEndpoint"] ?? "";
    return new TableClient(new Uri(storageEndpoint), "conversations", credential);
});

builder.Services.AddKeyedSingleton<TableClient>("feedback", (_, _) =>
{
    var storageEndpoint = builder.Configuration["Storage:TableEndpoint"] ?? "";
    return new TableClient(new Uri(storageEndpoint), "feedback", credential);
});

builder.Services.AddSingleton(_ =>
{
    var blobEndpoint = builder.Configuration["Storage:BlobEndpoint"] ?? "";
    return new BlobContainerClient(new Uri($"{blobEndpoint}/data"), credential);
});

// Pipeline services
builder.Services.AddSingleton<IQueryPreprocessor, QueryPreprocessor>();
builder.Services.AddSingleton<ISearchService, SearchService>();
builder.Services.AddSingleton<IContextAssembler, ContextAssembler>();
builder.Services.AddSingleton<IPromptBuilder, PromptBuilder>();
builder.Services.AddSingleton<IChatService, ChatService>();
builder.Services.AddSingleton<IResponseFormatter, ResponseFormatter>();
builder.Services.AddSingleton<IEmbeddingService, EmbeddingService>();

// Support services
builder.Services.AddSingleton<IConversationStore, ConversationStore>();
builder.Services.AddSingleton<IGameService, GameService>();
builder.Services.AddSingleton<IFeedbackService>(sp =>
{
    var tableClient = sp.GetRequiredKeyedService<TableClient>("feedback");
    return new FeedbackService(tableClient);
});

// Auth services
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();

var app = builder.Build();

// Middleware pipeline
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Map endpoints
var api = app.MapGroup("/api");
HealthEndpoints.Map(api);
ChatEndpoints.Map(api);
SearchEndpoints.Map(api);
GameEndpoints.Map(api);
FeedbackEndpoints.Map(api);
AuthEndpoints.Map(api);

// Map SignalR hub
app.MapHub<ChatHub>("/hubs/chat");

app.Run();
