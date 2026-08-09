using NSubstitute;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using PinballWizard.Infrastructure.Resolution;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Resolution;

public sealed class CosmosMachineAliasCatalogTests
{
    [Fact]
    public async Task MachineExistsAsync_ReturnsFalse_WhenManufacturerDiffers()
    {
        var machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Machine
                {
                    Id = "GweeP-MW95j", PartitionKey = "stern",
                    ManufacturerDisplayName = "stern", Title = "Godzilla (Pro)",
                },
            }.ToAsyncEnumerable());

        var catalog = new CosmosMachineAliasCatalog(machineRepo);

        Assert.True(await catalog.MachineExistsAsync("GweeP-MW95j", "stern", CancellationToken.None));
        Assert.False(await catalog.MachineExistsAsync("GweeP-MW95j", "sega", CancellationToken.None));
    }

    [Fact]
    public async Task GroupExistsAsync_ReturnsTrue_WhenGroupAndManufacturerMatch()
    {
        var machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Machine
                {
                    Id = "GweeP-MW95j", PartitionKey = "stern",
                    ManufacturerDisplayName = "Stern Pinball", Title = "Godzilla (Pro)",
                    GroupId = "GweeP",
                },
            }.ToAsyncEnumerable());

        var catalog = new CosmosMachineAliasCatalog(machineRepo);

        Assert.True(await catalog.GroupExistsAsync("GweeP", "stern", CancellationToken.None));
        Assert.False(await catalog.GroupExistsAsync("GweeP", "sega", CancellationToken.None));
        Assert.False(await catalog.GroupExistsAsync("OTHER", "stern", CancellationToken.None));
    }

    [Fact]
    public async Task StreamsOnce_CachesResults_OnSubsequentCalls()
    {
        var machineRepo = Substitute.For<IMachineRepository>();
        machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Machine
                {
                    Id = "GweeP-MW95j", PartitionKey = "stern",
                    ManufacturerDisplayName = "stern", Title = "Godzilla (Pro)",
                },
            }.ToAsyncEnumerable());

        var catalog = new CosmosMachineAliasCatalog(machineRepo);

        // Call twice.
        _ = await catalog.MachineExistsAsync("GweeP-MW95j", "stern", CancellationToken.None);
        _ = await catalog.MachineExistsAsync("GweeP-MW95j", "stern", CancellationToken.None);

        // StreamAllAsync must have been called exactly once — confirms the cache is used.
        machineRepo.Received(1).StreamAllAsync(Arg.Any<CancellationToken>());
    }
}
