using PinballWizard.Core.Configuration;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Core.Tests.Configuration;

public sealed class RagIngestionOptionsTests
{
    [Fact]
    public void Default_AcceptedTypes_IncludeFeatureMatrix()
    {
        var accepted = new RagIngestionOptions().AcceptedDocumentTypes;
        Assert.Contains(DocumentType.Manual, accepted);
        Assert.Contains(DocumentType.ServiceBulletin, accepted);
        Assert.Contains(DocumentType.FeatureMatrix, accepted);
    }

    // ADR-0042: Rulesheet added to allow-list so gameplay-rule PDFs are indexed.
    [Fact]
    public void Default_AcceptedTypes_IncludeRulesheet()
    {
        var accepted = new RagIngestionOptions().AcceptedDocumentTypes;
        Assert.Contains(DocumentType.Rulesheet, accepted);
    }
}
