using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using PinballWizard.Web.Components.Pages.Admin;
using Xunit;

namespace PinballWizard.Web.Tests.Components.Admin;

// bUnit tests for AdminCountValue.razor — the dashboard count-state component.
// Asserts the three mutually-exclusive states (number / loading / visible error)
// so the Invariant #17 failure path (a real error glyph, never a silent dash) is
// behaviourally pinned, not just structurally present.
public sealed class AdminCountValueTests : AsyncBunitContext
{
    public AdminCountValueTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Success_RendersCountNumber()
    {
        var cut = Render<AdminCountValue>(p => p
            .Add(x => x.TestId, "c")
            .Add(x => x.Loading, false)
            .Add(x => x.Failed, false)
            .Add(x => x.Count, 42));

        var el = cut.Find("[data-testid='c']");
        Assert.Equal("42", el.TextContent.Trim());
    }

    [Fact]
    public void Failed_RendersErrorSentinel_NotANumber()
    {
        var cut = Render<AdminCountValue>(p => p
            .Add(x => x.TestId, "c")
            .Add(x => x.Loading, false)
            .Add(x => x.Failed, true)
            .Add(x => x.Count, (int?)null));

        // Visible error glyph present...
        _ = cut.Find("[data-testid='c-error']");
        // ...and the number sentinel is absent (no silent dash / fabricated 0).
        Assert.Empty(cut.FindAll("[data-testid='c']"));
    }

    [Fact]
    public void Loading_RendersCountSentinelWithoutThrowing()
    {
        var cut = Render<AdminCountValue>(p => p
            .Add(x => x.TestId, "c")
            .Add(x => x.Loading, true)
            .Add(x => x.Failed, false)
            .Add(x => x.Count, (int?)null));

        cut.Find("[data-testid='c']");
    }
}
