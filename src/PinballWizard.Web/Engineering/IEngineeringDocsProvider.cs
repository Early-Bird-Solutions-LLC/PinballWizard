namespace PinballWizard.Web.Engineering;

public interface IEngineeringDocsProvider
{
    IReadOnlyList<EngineeringDoc> Docs { get; }

    EngineeringDoc? BySlug(string slug);

    IReadOnlyList<AdrEntry> Adrs { get; }

    AdrEntry? ByNumber(int number);

    string SourceCommit { get; }

    string BuildDate { get; }
}
