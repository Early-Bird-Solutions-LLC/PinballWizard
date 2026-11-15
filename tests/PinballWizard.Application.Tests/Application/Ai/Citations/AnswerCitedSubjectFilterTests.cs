using PinballWizard.Application.Ai;
using PinballWizard.Application.Ai.Citations;
using Xunit;

namespace PinballWizard.Application.Tests.Application.Ai.Citations;

public sealed class AnswerCitedSubjectFilterTests
{
    [Fact]
    public void Filter_DropsCitationWhoseSubjectIsAbsentFromAnswer()
    {
        // #783 fixture: recommendation names Hobbit / Halloween / … but the
        // tool trace also returned The Jetsons Metadata — must not show.
        var answer =
            "Similarly themed games include The Hobbit, Halloween, " +
            "The Texas Chainsaw Massacre, Evil Dead, and Barnyard.";

        var citations = new[]
        {
            Cite("The Hobbit — Metadata", "https://opdb.org/machines/hobbit"),
            Cite("Halloween — Metadata", "https://opdb.org/machines/halloween"),
            Cite("The Jetsons — Metadata", "https://opdb.org/machines/jetsons"),
        };

        var result = AnswerCitedSubjectFilter.Filter(answer, citations);

        Assert.Equal(2, result.Kept.Count);
        Assert.Equal(1, result.DroppedCount);
        Assert.DoesNotContain(result.Kept, c => c.Title.Contains("Jetsons", StringComparison.Ordinal));
        Assert.Contains(result.Kept, c => c.Title.Contains("Hobbit", StringComparison.Ordinal));
        Assert.Contains(result.Kept, c => c.Title.Contains("Halloween", StringComparison.Ordinal));
    }

    [Fact]
    public void Filter_KeepsCitationWhenSubjectAppearsWithoutLeadingArticle()
    {
        var answer = "Jetsons is a rare licensed table from the early 90s.";
        var citations = new[]
        {
            Cite("The Jetsons — Metadata", "https://opdb.org/machines/jetsons"),
        };

        var result = AnswerCitedSubjectFilter.Filter(answer, citations);

        Assert.Equal(citations, result.Kept);
        Assert.Equal(0, result.DroppedCount);
    }

    [Fact]
    public void Filter_KeepsInheritedCitationsEvenWhenSubjectAbsent()
    {
        // Clarifying follow-up ("how many balls?") may never restate the
        // machine name — inherited grounding must survive (#783 vs #780).
        var citations = new[]
        {
            Cite("Medieval Madness — Rules", "https://example.com/mm.pdf", inherited: true),
        };

        var result = AnswerCitedSubjectFilter.Filter("Three balls in multiball.", citations);

        Assert.Equal(citations, result.Kept);
        Assert.Equal(0, result.DroppedCount);
    }

    [Fact]
    public void Filter_KeepsOpdbRecordTitlesWithoutMachineName()
    {
        var citations = new[]
        {
            Cite("OPDB record GRBE-MJL05", "https://opdb.org/machines/GRBE-MJL05"),
        };

        var result = AnswerCitedSubjectFilter.Filter("A grounded answer with no title echo.", citations);

        Assert.Equal(citations, result.Kept);
        Assert.Equal(0, result.DroppedCount);
    }

    [Fact]
    public void Filter_KeepsPageAnchoredCorpusTitleWhenMachineNamed()
    {
        var answer = "On Godzilla, the Godzilla multiball starts when you shoot the left orbit.";
        var citations = new[]
        {
            Cite("Godzilla (p. 12–14)", "https://example.com/godzilla.pdf"),
        };

        var result = AnswerCitedSubjectFilter.Filter(answer, citations);

        Assert.Single(result.Kept);
        Assert.Equal(0, result.DroppedCount);
    }

    [Fact]
    public void ExtractSubject_SplitsEmDashAndPageRange()
    {
        Assert.Equal("The Jetsons", AnswerCitedSubjectFilter.ExtractSubject("The Jetsons — Metadata"));
        Assert.Equal("Godzilla", AnswerCitedSubjectFilter.ExtractSubject("Godzilla (p. 12–14)"));
        Assert.Equal(
            "Medieval Madness",
            AnswerCitedSubjectFilter.ExtractSubject(
                "Market value · Medieval Madness — Silverball Labs / PinballPrices.com"));
        Assert.Null(AnswerCitedSubjectFilter.ExtractSubject("OPDB record GRBE-MJL05"));
        Assert.Null(AnswerCitedSubjectFilter.ExtractSubject("Match Play standings · Pinburgh 2026"));
    }

    [Fact]
    public void Filter_KeepsKineticistGuideLinksWhenMachineNamed()
    {
        // Bugbot: curated titles are "Label · {machine}" — subject is the
        // machine on the right, not the full phrase.
        var answer = "For Godzilla rules, start with the Kineticist tutorial.";
        var citations = new[]
        {
            Cite("Kineticist tutorial · Godzilla", "https://kineticist.com/godzilla", CitationSourceType.CuratedLink),
            Cite("Game rules · Godzilla", "https://kineticist.com/godzilla/rules", CitationSourceType.CuratedLink),
            Cite("Pinball primer · Godzilla", "https://kineticist.com/primer", CitationSourceType.CuratedLink),
            Cite("Kineticist tutorial · The Jetsons", "https://kineticist.com/jetsons", CitationSourceType.CuratedLink),
        };

        var result = AnswerCitedSubjectFilter.Filter(answer, citations);

        Assert.Equal(3, result.Kept.Count);
        Assert.Equal(1, result.DroppedCount);
        Assert.DoesNotContain(result.Kept, c => c.Title.Contains("Jetsons", StringComparison.Ordinal));
    }

    [Fact]
    public void Filter_KeepsEditionQualifiedTitleWhenAnswerNamesBaseGame()
    {
        // Bugbot: "Godzilla (Pro)" must match answer prose that just says
        // "Godzilla" — editions are retrieval variants, not distinct subjects.
        var answer = "On Godzilla, flippers persist through multiball.";
        var citations = new[]
        {
            Cite("Godzilla (Pro) — Rules", "https://example.com/godzilla-pro.pdf"),
            Cite("Godzilla (Premium) (p. 4–6)", "https://example.com/godzilla-prem.pdf"),
        };

        var result = AnswerCitedSubjectFilter.Filter(answer, citations);

        Assert.Equal(2, result.Kept.Count);
        Assert.Equal(0, result.DroppedCount);
        Assert.Equal("Godzilla", AnswerCitedSubjectFilter.ExtractSubject("Godzilla (Pro) — Rules"));
        Assert.Equal("Godzilla", AnswerCitedSubjectFilter.ExtractSubject("Godzilla (Premium) (p. 4–6)"));
    }

    [Fact]
    public void Filter_EmptyInput_IsNoOp()
    {
        var empty = Array.Empty<Citation>();
        var result = AnswerCitedSubjectFilter.Filter("anything", empty);
        Assert.Empty(result.Kept);
        Assert.Equal(0, result.DroppedCount);
    }

    private static Citation Cite(
        string title,
        string url,
        CitationSourceType sourceType = CitationSourceType.MachineRecord,
        bool inherited = false)
        => new(
            Title: title,
            SourceUrl: url,
            SourceType: sourceType,
            Inherited: inherited);
}
