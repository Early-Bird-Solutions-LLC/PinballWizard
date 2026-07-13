using PinballWizard.Application.Resolution;
using Xunit;

namespace PinballWizard.Application.Tests.Resolution;

public class MachineTextNormalizerTests
{
    // static readonly to satisfy CA1861 (warnaserror in this repo)
    private static readonly string[] HotWheelsTokens = ["hot", "wheels"];

    [Theory]
    // separators collapse to a single space
    [InlineData("Hot-Wheels", "hot wheels")]
    [InlineData("Hot_Wheels", "hot wheels")]
    [InlineData("Hot--Wheels", "hot wheels")]
    // camelCase splits; an already-joined word does NOT
    [InlineData("HotWheels", "hot wheels")]
    [InlineData("Hotwheels", "hotwheels")]
    // subtitle punctuation
    [InlineData("Houdini: Master of Mystery", "houdini master of mystery")]
    // ampersand folds to "and" — this is the divergence the &/and retry loop existed to bridge
    [InlineData("Bally & Williams", "bally and williams")]
    [InlineData("Bally and Williams", "bally and williams")]
    // apostrophes vanish rather than splitting
    [InlineData("Guns N' Roses", "guns n roses")]
    [InlineData("Barry O's Barbeque Challenge", "barry os barbeque challenge")]
    // slashes are separators
    [InlineData("AC/DC", "ac dc")]
    // diacritics fold
    [InlineData("Café", "cafe")]
    // digit/letter boundaries
    [InlineData("DOC0018-00-REV-A", "doc 0018 00 rev a")]
    // real AP filenames
    [InlineData("GTF-Quick-Reference-Guide", "gtf quick reference guide")]
    [InlineData("API-Houdini-Service-Manual-10-6-21", "api houdini service manual 10 6 21")]
    public void Key_NormalizesToCanonicalForm(string input, string expected)
        => Assert.Equal(expected, MachineTextNormalizer.Key(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void Tokenize_EmptyOrSeparatorOnly_ReturnsEmpty(string? input)
        => Assert.Empty(MachineTextNormalizer.Tokenize(input));

    [Fact]
    public void Tokenize_ReturnsTokens_NotAJoinedString()
        => Assert.Equal(HotWheelsTokens, MachineTextNormalizer.Tokenize("Hot-Wheels"));
}
