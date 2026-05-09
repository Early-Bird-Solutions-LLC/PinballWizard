// PinballWizard.Web.Client — WebAssembly client for Blazor auto-render mode.
//
// Per ADR-0026 § 1 Blazor Web App with auto-render mode. Components
// authored with @rendermode InteractiveAuto first render via the
// Server (instant interactivity, no WASM download wait) then
// migrate to WebAssembly after the runtime downloads in the
// background — at which point the same component runs client-side
// without round-trips. The Server project (PinballWizard.Web)
// references this project via AddAdditionalAssemblies so the
// router can find Client-side @page components.
//
// Wave 1 PR-F0: skeleton only. No client-specific services
// registered yet — MudBlazor is referenced for WASM compilation but
// the AddMudServices() call lives on the Server side because the
// Server-rendered prerender pass needs the services first. Components
// using MudBlazor work in both render modes.

using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

await builder.Build().RunAsync().ConfigureAwait(false);
