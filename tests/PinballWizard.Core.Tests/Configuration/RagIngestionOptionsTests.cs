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

    // SdkGuide added so P3 SDK developer docs indexed via --sync-p3-sdk-docs
    // are admitted to the RAG pipeline (Multimorphic P3 SDK ingest, Issue #540).
    [Fact]
    public void Default_AcceptedTypes_IncludeSdkGuide()
    {
        var accepted = new RagIngestionOptions().AcceptedDocumentTypes;
        Assert.Contains(DocumentType.SdkGuide, accepted);
    }
}
