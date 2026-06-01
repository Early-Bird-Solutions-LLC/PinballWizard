using PinballWizard.Application.Linking;
using PinballWizard.Core.Domain;
using Xunit;

namespace PinballWizard.Application.Tests.Linking;

/// <summary>
/// Tests for <see cref="EditionResolver"/> — resolving a per-edition document
/// to the edition-correct base machine in a same-franchise candidate set.
/// </summary>
public sealed class EditionResolverTests
{
    // ── Filename token extraction ────────────────────────────────────────

    [Theory]
    [InlineData("Godzilla_Pro_web.pdf", "pro")]
    [InlineData("GODZILLA-PRO-New-Address-compressed.pdf", "pro")]
    [InlineData("Godzilla_LE_Pre_web.pdf", "le")]
    [InlineData("GODZILLA-PREM-New-Address-compressed.pdf", "premium")]
    [InlineData("Godzilla_70th_web.pdf", "70th")]
    public void ExtractEditionToken_FromFilename(string filename, string expected)
    {
        Assert.Equal(expected, EditionResolver.ExtractEditionToken(filename));
    }

    [Theory]
    [InlineData("Godzilla-Pinball-Feature-Matrix-3kjhasdf.pdf")]
    [InlineData("Godzilla-Rulesheet.pdf")]
    public void ExtractEditionToken_GroupLevelDoc_ReturnsNull(string filename)
    {
        Assert.Null(EditionResolver.ExtractEditionToken(filename));
    }

    [Theory]
    [InlineData("Godzilla-Pinball-Feature-Matrix-3kjhasdf.pdf", true)]
    [InlineData("Godzilla-Rulesheet.pdf", true)]
    [InlineData("Godzilla_Pro_web.pdf", false)]
    public void IsGroupLevelDoc(string filename, bool expected)
    {
        Assert.Equal(expected, EditionResolver.IsGroupLevelDoc(filename));
    }
}
