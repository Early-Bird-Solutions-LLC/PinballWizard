using PinballWizard.Application.Jobs;
using PinballWizard.Infrastructure.Jobs;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Jobs;

// Unit tests for ArmJobAdminService and its companion helper extension.
//
// The ARM SDK types (ArmClient, ContainerAppJobResource, etc.) are sealed
// concrete classes — there is no interface seam for mocking at the ArmClient
// level without a live Azure environment. Tests therefore cover the two
// independently-testable seams that carry real business logic:
//
//   1. ArmJobAdminService.DeriveDisplayName (internal static, tested via
//      InternalsVisibleTo on the Infrastructure csproj) — the naming
//      transformation that produces human-readable labels from ARM job names.
//
//   2. Infrastructure.Jobs.ServiceCollectionExtensions.ParseSubAndRg
//      (internal static) — sub/RG extraction from a Cosmos ARM resource ID.
//
// Live ARM path tests (ListJobsAsync / StartJobAsync) are E2E tests run
// against the deployed environment per the test-tier decision (DL-0002).
// The ArmJobAdminException surface (correct type thrown on RequestFailed)
// would require faking the sealed SDK — too high a maintenance cost for the
// benefit; the mapping is exercised by the E2E job.
public sealed class ArmJobAdminServiceTests
{
    // ── DeriveDisplayName ────────────────────────────────────────────────────

    [Theory]
    [InlineData("pinwiz-job-linker-buutj", "Linker")]
    [InlineData("pinwiz-job-opdb-buutj", "Opdb")]
    [InlineData("pinwiz-job-stern-refresh-buutj", "Stern Refresh")]
    public void DeriveDisplayName_KnownJobNames_ProduceTitleCasedLabels(
        string jobName, string expected)
    {
        var result = ArmJobAdminService.DeriveDisplayName(jobName);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void DeriveDisplayName_UnknownPrefix_ReturnsJobNameAsIs()
    {
        // A job that doesn't follow the pinwiz-job-* convention is returned verbatim.
        const string unrecognized = "some-other-job-name";
        var result = ArmJobAdminService.DeriveDisplayName(unrecognized);
        Assert.Equal(unrecognized, result);
    }

    [Fact]
    public void DeriveDisplayName_SingleSegment_ReturnsCapitalized()
    {
        // pinwiz-job-X-suffix → X (capitalized)
        var result = ArmJobAdminService.DeriveDisplayName("pinwiz-job-linker-abc12");
        Assert.Equal("Linker", result);
    }

    [Fact]
    public void DeriveDisplayName_MultiSegment_TitleCasesEachWord()
    {
        // pinwiz-job-stern-refresh-suffix → "Stern Refresh"
        var result = ArmJobAdminService.DeriveDisplayName("pinwiz-job-stern-refresh-xyz99");
        Assert.Equal("Stern Refresh", result);
    }

    // ── ParseSubAndRg ────────────────────────────────────────────────────────

    [Fact]
    public void ParseSubAndRg_ValidCosmosResourceId_ExtractsCorrectValues()
    {
        const string resourceId =
            "/subscriptions/b1f33f17-abcd-1234-efgh-000000000000" +
            "/resourceGroups/pinwiz-rg-dev" +
            "/providers/Microsoft.DocumentDB/databaseAccounts/pinwiz-cosmos-dev";

        var (sub, rg) = ServiceCollectionExtensions.ParseSubAndRg(resourceId);

        Assert.Equal("b1f33f17-abcd-1234-efgh-000000000000", sub);
        Assert.Equal("pinwiz-rg-dev", rg);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseSubAndRg_NullOrEmpty_ReturnsBothNull(string? resourceId)
    {
        var (sub, rg) = ServiceCollectionExtensions.ParseSubAndRg(resourceId);
        Assert.Null(sub);
        Assert.Null(rg);
    }

    [Fact]
    public void ParseSubAndRg_MissingResourceGroup_ReturnsNullRg()
    {
        const string noRg = "/subscriptions/b1f33f17-abcd-1234-efgh-000000000000";
        var (sub, rg) = ServiceCollectionExtensions.ParseSubAndRg(noRg);
        Assert.Equal("b1f33f17-abcd-1234-efgh-000000000000", sub);
        Assert.Null(rg);
    }

    [Fact]
    public void ParseSubAndRg_MissingSubscription_ReturnsNullSub()
    {
        const string noSub = "/resourceGroups/pinwiz-rg-dev/providers/foo/bar";
        var (sub, rg) = ServiceCollectionExtensions.ParseSubAndRg(noSub);
        Assert.Null(sub);
        Assert.Equal("pinwiz-rg-dev", rg);
    }

    // ── ArmJobAdminException ─────────────────────────────────────────────────

    [Fact]
    public void ArmJobAdminException_PreservesInnerException()
    {
        var inner = new InvalidOperationException("root cause");
        var ex = new ArmJobAdminException("ARM failed", inner);

        Assert.Equal("ARM failed", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    [Fact]
    public void ArmJobAdminException_WithoutInner_HasNullInner()
    {
        var ex = new ArmJobAdminException("ARM unavailable");
        Assert.Equal("ARM unavailable", ex.Message);
        Assert.Null(ex.InnerException);
    }
}
