using Microsoft.Extensions.DependencyInjection;
using PinballWizard.Application.Catalog;
using PinballWizard.Application.Persistence;
using Xunit;

namespace PinballWizard.Web.Tests.A11y;

// Pins that AddAdminTestDoubles registers resolvable stubs returning the seed
// fixture the admin pages render against. If a new admin-page dependency is
// added without a double, the page won't render in the axe/circuit hosts —
// this catches the missing registration directly.
public sealed class AdminTestDoublesTests
{
    [Fact]
    public async Task AddAdminTestDoubles_RegistersResolvableStats_WithSeedFamily()
    {
        var sp = new ServiceCollection().AddAdminTestDoubles().BuildServiceProvider();

        var stats = sp.GetRequiredService<ICatalogStatsReadRepository>();
        var mfrs = new List<ManufacturerCatalogStats>();
        await foreach (var m in stats.StreamAllManufacturersAsync(CancellationToken.None))
            mfrs.Add(m);

        Assert.Single(mfrs);
        Assert.Equal("stern", mfrs[0].Manufacturer);
        // Godzilla family: one machine with docs, one with zero (edition gap).
        Assert.Contains(mfrs[0].Machines, m => m.MachineId == "mch_godzilla_pro" && m.DocCount == 2);
        Assert.Contains(mfrs[0].Machines, m => m.MachineId == "mch_godzilla_le" && m.DocCount == 0);
    }

    [Fact]
    public void AddAdminTestDoubles_RegistersAllAdminPageDependencies()
    {
        var sp = new ServiceCollection().AddAdminTestDoubles().BuildServiceProvider();

        // Every service injected by an /admin/* page must resolve.
        Assert.NotNull(sp.GetService<ICatalogStatsReadRepository>());
        Assert.NotNull(sp.GetService<IMachineRepository>());
        Assert.NotNull(sp.GetService<IMachineDocumentReadRepository>());
        Assert.NotNull(sp.GetService<IRawDocumentRepository>());
        Assert.NotNull(sp.GetService<PinballWizard.Application.Linking.IDocumentLinker>());
        Assert.NotNull(sp.GetService<ILinkOverrideRepository>());
        Assert.NotNull(sp.GetService<IIngestionSourceRepository>());
        Assert.NotNull(sp.GetService<IAdminSettingsRepository>());
        Assert.NotNull(sp.GetService<IAgentPromptOverrideRepository>());
        Assert.NotNull(sp.GetService<PinballWizard.Application.Ai.EmbeddedResourceAgentPromptProvider>());
        Assert.NotNull(sp.GetService<Microsoft.Extensions.Options.IOptions<PinballWizard.Core.Configuration.AiFoundryOptions>>());
    }
}
