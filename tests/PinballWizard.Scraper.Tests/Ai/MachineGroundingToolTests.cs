using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Ai.Tools;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Scraper.Tests.Ai;

public sealed class MachineGroundingToolTests
{
    [Fact]
    public async Task GetMachineByTitleAsync_ReturnsFirstMatch_MappedToDto()
    {
        var machine = new Machine
        {
            Id = "GRBN-MQR4P",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball",
            Title = "Foo Fighters",
            Year = 2023,
            Themes = ["rock", "music"],
            Designers = ["George Gomez"],
            OpdbSourceUrl = "https://opdb.org/machines/GRBN-MQR4P",
            Editions =
            [
                new MachineEdition { Name = "Pro", Msrp = "$7,000" },
                new MachineEdition { Name = "Premium", Msrp = "$9,500" },
            ],
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Foo Fighters", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(machine));

        var tool = new MachineGroundingTool(repo, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Foo Fighters", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-MQR4P", result!.OpdbId);
        Assert.Equal("Foo Fighters", result.Title);
        Assert.Equal("Stern Pinball", result.Manufacturer);
        Assert.Equal(2023, result.Year);
        Assert.Equal(["rock", "music"], result.Themes);
        Assert.Equal(["George Gomez"], result.Designers);
        Assert.Equal("https://opdb.org/machines/GRBN-MQR4P", result.OpdbSourceUrl);
        Assert.Equal(2, result.Editions.Count);
        Assert.Equal("Pro", result.Editions[0].Name);
        Assert.Equal("$7,000", result.Editions[0].Msrp);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_NoMatch_ReturnsNull()
    {
        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("nonexistent", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable<Machine>());

        var tool = new MachineGroundingTool(repo, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("nonexistent", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_EmptyTitle_ReturnsNullWithoutQuerying()
    {
        var repo = Substitute.For<IMachineRepository>();

        var tool = new MachineGroundingTool(repo, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync(string.Empty, CancellationToken.None);

        Assert.Null(result);
        repo.DidNotReceiveWithAnyArgs().QueryByTitleAsync(default!, default);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_WhitespaceTitle_ReturnsNullWithoutQuerying()
    {
        var repo = Substitute.For<IMachineRepository>();

        var tool = new MachineGroundingTool(repo, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("   ", CancellationToken.None);

        Assert.Null(result);
        repo.DidNotReceiveWithAnyArgs().QueryByTitleAsync(default!, default);
    }

    [Fact]
    public async Task GetMachineByTitleAsync_MultipleMatches_ReturnsFirst()
    {
        var first = NewMachine("GRBN-MQR4P", "Foo Fighters", 2023);
        var second = NewMachine("GRBN-XYZZZ", "Foo Fighters", 1992); // hypothetical re-issue

        var repo = Substitute.For<IMachineRepository>();
        repo.QueryByTitleAsync("Foo Fighters", Arg.Any<CancellationToken>())
            .Returns(ToAsyncEnumerable(first, second));

        var tool = new MachineGroundingTool(repo, NullLogger<MachineGroundingTool>.Instance);
        var result = await tool.GetMachineByTitleAsync("Foo Fighters", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("GRBN-MQR4P", result!.OpdbId);
    }

    [Fact]
    public void Ctor_NullRepository_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MachineGroundingTool(null!, NullLogger<MachineGroundingTool>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var repo = Substitute.For<IMachineRepository>();
        Assert.Throws<ArgumentNullException>(() =>
            new MachineGroundingTool(repo, null!));
    }

    private static Machine NewMachine(string id, string title, int year) => new()
    {
        Id = id,
        PartitionKey = "stern",
        ManufacturerDisplayName = "Stern Pinball",
        Title = title,
        Year = year,
        FirstSeenAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
    };

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(params T[] items)
    {
        foreach (var item in items)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }
}
