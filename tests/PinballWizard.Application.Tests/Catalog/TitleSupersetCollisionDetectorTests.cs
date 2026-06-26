using PinballWizard.Application.Catalog;
using Xunit;

namespace PinballWizard.Application.Tests.Catalog;

/// <summary>
/// Unit tests for <see cref="TitleSupersetCollisionDetector"/> — the catalog
/// audit that surfaces the title-superset shape behind the #532 mis-grounding
/// class (e.g. "Iron Maiden" vs "Iron Maiden: Legacy of the Beast").
/// </summary>
public sealed class TitleSupersetCollisionDetectorTests
{
    [Fact]
    public void Detect_SubtitleSuperset_DifferentGroup_IsCollision()
    {
        var machines = new (string, string?)[]
        {
            ("Iron Maiden", "G4yZN"),
            ("Iron Maiden: Legacy of the Beast", "G4dOQ"),
        };

        var collisions = TitleSupersetCollisionDetector.Detect(machines);

        var c = Assert.Single(collisions);
        Assert.Equal("Iron Maiden", c.ShorterTitle);
        Assert.Equal("G4yZN", c.ShorterGroupId);
        Assert.Equal("Iron Maiden: Legacy of the Beast", c.LongerTitle);
        Assert.Equal("G4dOQ", c.LongerGroupId);
    }

    [Fact]
    public void Detect_DashSubtitleSeparator_IsCollision()
    {
        var machines = new (string, string?)[]
        {
            ("Whirlwind", "Gaaaa"),
            ("Whirlwind - Special Edition", "Gbbbb"),
        };

        Assert.Single(TitleSupersetCollisionDetector.Detect(machines));
    }

    [Fact]
    public void Detect_SameGroupEditions_DoNotCollide()
    {
        // Pro/Premium/LE of the same game share a group — never a collision,
        // even though the titles differ in length.
        var machines = new (string, string?)[]
        {
            ("Godzilla", "Gz111"),
            ("Godzilla: Limited Edition", "Gz111"),
        };

        Assert.Empty(TitleSupersetCollisionDetector.Detect(machines));
    }

    [Fact]
    public void Detect_BareWordPrefixWithoutSubtitleSeparator_IsNotCollision()
    {
        // "Iron" is a word-prefix of "Iron Maiden" but there is no subtitle
        // separator, so an agent could not "shorten" to it — not a collision.
        var machines = new (string, string?)[]
        {
            ("Iron", "Gi111"),
            ("Iron Maiden", "Gi222"),
        };

        Assert.Empty(TitleSupersetCollisionDetector.Detect(machines));
    }

    [Fact]
    public void Detect_IsCaseInsensitive()
    {
        var machines = new (string, string?)[]
        {
            ("STAR WARS", "Gsw01"),
            ("star wars: fall of the empire", "Gsw99"),
        };

        Assert.Single(TitleSupersetCollisionDetector.Detect(machines));
    }

    [Fact]
    public void Detect_OneShorterMatchingMultipleLongers_ReturnsAllPairs()
    {
        var machines = new (string, string?)[]
        {
            ("Star Trek", "Gst00"),
            ("Star Trek: The Next Generation", "Gst11"),
            ("Star Trek: Enterprise", "Gst22"),
        };

        var collisions = TitleSupersetCollisionDetector.Detect(machines);

        Assert.Equal(2, collisions.Count);
        Assert.All(collisions, c => Assert.Equal("Star Trek", c.ShorterTitle));
    }

    [Fact]
    public void Detect_BlankTitleOrGroup_IsIgnored()
    {
        var machines = new (string, string?)[]
        {
            ("Iron Maiden", "G4yZN"),
            ("Iron Maiden: Legacy of the Beast", null),   // no group → ignored
            ("", "Gx"),                                    // blank title → ignored
        };

        Assert.Empty(TitleSupersetCollisionDetector.Detect(machines));
    }

    [Fact]
    public void Detect_NoCollisions_ReturnsEmpty()
    {
        var machines = new (string, string?)[]
        {
            ("Medieval Madness", "Gmm01"),
            ("Attack from Mars", "Gafm0"),
        };

        Assert.Empty(TitleSupersetCollisionDetector.Detect(machines));
    }
}
