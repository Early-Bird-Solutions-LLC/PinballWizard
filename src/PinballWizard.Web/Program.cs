using MudBlazor.Services;
using PinballWizard.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddSingleton<IChatService, MockChatService>();
builder.Services.AddSingleton<IGameCatalogService, MockGameCatalogService>();
builder.Services.AddSingleton<IConversationStore, MockConversationStore>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<PinballWizard.Web.App>()
    .AddInteractiveServerRenderMode();

app.Run();
