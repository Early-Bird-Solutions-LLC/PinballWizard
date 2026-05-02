using PinballWizard.Core.Domain;
using PinballWizard.Infrastructure.Integrations.Opdb;
using Xunit;

namespace PinballWizard.Scraper.Tests.Integrations.Opdb;

/// <summary>
/// Unit tests for <see cref="OpdbMachineMapper"/>: mapping OPDB DTOs
/// to <see cref="Machine"/> aggregates and merging fresh OPDB data
/// onto existing records.
/// </summary>
public sealed class OpdbMachineMapperTests
{
    private static readonly DateTimeOffset NowFixed = new(2026, 5, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Map_NotMachine_ReturnsNull()
    {
        var dto = new OpdbMachineDto { OpdbId = "GRBN-MQR4P", IsMachine = false };
        Assert.Null(OpdbMachineMapper.Map(dto, NowFixed));
    }

    [Fact]
    public void Map_MissingOpdbId_ReturnsNull()
    {
        var dto = new OpdbMachineDto
        {
            IsMachine = true,
            Manufacturer = new OpdbManufacturerDto { Name = "Stern Pinball, Inc." },
        };
        Assert.Null(OpdbMachineMapper.Map(dto, NowFixed));
    }

    [Fact]
    public void Map_MissingManufacturer_ReturnsNull()
    {
        var dto = new OpdbMachineDto { OpdbId = "GRBN-MQR4P", IsMachine = true };
        Assert.Null(OpdbMachineMapper.Map(dto, NowFixed));
    }

    [Fact]
    public void Map_HappyPath_PopulatesEveryMappedField()
    {
        var dto = new OpdbMachineDto
        {
            OpdbId = "GRBN-MQR4P",
            IsMachine = true,
            Name = "Stranger Things (Pro)",
            CommonName = "Stranger Things",
            Manufacturer = new OpdbManufacturerDto { Name = "Stern Pinball, Inc.", ShortName = "Stern" },
            ManufactureDate = "2019-05-01",
            Designers = [new OpdbPersonDto { PersonId = 1, Name = "Brian Eddy" }],
            Keywords = ["TV", "Horror", "1980s"],
        };

        var machine = OpdbMachineMapper.Map(dto, NowFixed);

        Assert.NotNull(machine);
        Assert.Equal("GRBN-MQR4P", machine!.Id);
        Assert.Equal("stern", machine.PartitionKey);
        Assert.Equal("Stern Pinball, Inc.", machine.ManufacturerDisplayName);
        Assert.Equal("Stranger Things", machine.Title);
        Assert.Equal(2019, machine.Year);
        Assert.Equal(["Brian Eddy"], machine.Designers);
        Assert.Equal(["TV", "Horror", "1980s"], machine.Themes);
        Assert.Equal("https://opdb.org/machines/GRBN-MQR4P", machine.OpdbSourceUrl);
        Assert.Equal(NowFixed, machine.FirstSeenAt);
        Assert.Equal(NowFixed, machine.LastSeenAt);
    }

    [Fact]
    public void Map_FallsBackToFullNameWhenCommonNameAbsent()
    {
        var dto = new OpdbMachineDto
        {
            OpdbId = "X",
            IsMachine = true,
            Name = "Foo Bar (Pro)",
            CommonName = null,
            Manufacturer = new OpdbManufacturerDto { Name = "Stern" },
        };

        var machine = OpdbMachineMapper.Map(dto, NowFixed);

        Assert.NotNull(machine);
        Assert.Equal("Foo Bar (Pro)", machine!.Title);
    }

    [Theory]
    [InlineData("Stern Pinball, Inc.", "stern")]
    [InlineData("Jersey Jack Pinball", "jjp")]
    [InlineData("American Pinball", "americanpinball")]
    [InlineData("Spooky Pinball", "spooky")]
    [InlineData("Multimorphic, Inc.", "multimorphic")]
    [InlineData("Chicago Gaming Co.", "cgc")]
    [InlineData("Haggis Pinball", "haggis")]
    [InlineData("Pinball Brothers", "pinballbrothers")]
    [InlineData("Dutch Pinball B.V.", "dutch")]
    [InlineData("Barrels of Fun", "barrelsoffun")]
    [InlineData("Some Unknown Maker", "someunknownmaker")]
    public void NormalizeManufacturerKey_ProducesExpectedKey(string input, string expected)
    {
        Assert.Equal(expected, OpdbMachineMapper.NormalizeManufacturerKey(input));
    }

    [Fact]
    public void Map_InvalidDate_LeavesYearNull()
    {
        var dto = MachineDto("X", manufactureDate: "not a date");
        var machine = OpdbMachineMapper.Map(dto, NowFixed);
        Assert.NotNull(machine);
        Assert.Null(machine!.Year);
    }

    [Fact]
    public void Map_YearOnlyDate_ParsesYear()
    {
        var dto = MachineDto("X", manufactureDate: "2017");
        var machine = OpdbMachineMapper.Map(dto, NowFixed);
        Assert.NotNull(machine);
        Assert.Equal(2017, machine!.Year);
    }

    [Fact]
    public void MergeOpdbFieldsInto_RefreshesOpdbFields_PreservesProjectFields()
    {
        var existingFirstSeen = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var existing = new Machine
        {
            Id = "GRBN-MQR4P",
            PartitionKey = "stern",
            ManufacturerDisplayName = "Stern Pinball, Inc.",
            Title = "Old Title",
            Year = 2018,
            Designers = ["Old Designer"],
            Themes = ["Old"],
            ManufacturerSlugs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["stern"] = "stranger-things",
            },
            Editions = [new MachineEdition { Name = "Pro" }],
            FirstSeenAt = existingFirstSeen,
            LastSeenAt = existingFirstSeen,
        };

        var fresh = new OpdbMachineDto
        {
            OpdbId = "GRBN-MQR4P",
            IsMachine = true,
            CommonName = "Stranger Things",
            Manufacturer = new OpdbManufacturerDto { Name = "Stern Pinball, Inc." },
            ManufactureDate = "2019-05-01",
            Designers = [new OpdbPersonDto { Name = "Brian Eddy" }],
            Keywords = ["TV"],
        };

        OpdbMachineMapper.MergeOpdbFieldsInto(existing, fresh, NowFixed);

        // OPDB-sourced fields refreshed
        Assert.Equal("Stranger Things", existing.Title);
        Assert.Equal(2019, existing.Year);
        Assert.Equal(["Brian Eddy"], existing.Designers);
        Assert.Equal(["TV"], existing.Themes);
        Assert.Equal(NowFixed, existing.LastSeenAt);

        // Project-owned fields preserved
        Assert.Equal(existingFirstSeen, existing.FirstSeenAt);
        Assert.Single(existing.ManufacturerSlugs);
        Assert.Equal("stranger-things", existing.ManufacturerSlugs["stern"]);
        Assert.Single(existing.Editions);
    }

    private static OpdbMachineDto MachineDto(string opdbId, string? manufactureDate = null) => new()
    {
        OpdbId = opdbId,
        IsMachine = true,
        Name = "Test",
        CommonName = "Test",
        Manufacturer = new OpdbManufacturerDto { Name = "Stern Pinball, Inc." },
        ManufactureDate = manufactureDate,
    };
}
