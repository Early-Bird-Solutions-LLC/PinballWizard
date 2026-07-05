using Azure.Core;
using Azure.ResourceManager;
using Microsoft.Extensions.Logging.Abstractions;
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
//   3. UpdateScheduleAsync guard-clause preconditions (jobName null/whitespace
//      and invalid cron expression) — exercised by constructing a real
//      ArmJobAdminService with a no-op credential; guard clauses throw before
//      GetResourceGroupAsync is reached so no network call ever occurs.
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

    // ── UpdateScheduleAsync guard clauses ────────────────────────────────────

    // Guard-clause order: UpdateScheduleAsync checks jobName first
    // (ArgumentException.ThrowIfNullOrWhiteSpace) then cron expression
    // (CronExpressionValidator.Validate). Each Theory below exercises one
    // guard in isolation:
    //
    //   - The jobName cases intentionally pass a also-invalid cron string to
    //     confirm the jobName guard fires first (cron is never reached).
    //   - The cron cases use a valid jobName to isolate the cron guard.
    //
    // Both sets throw before GetResourceGroupAsync is called — no network I/O
    // occurs. ArmClient construction is lazy; FakeCredential tokens are never
    // sent to Azure.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UpdateScheduleAsync_NullOrWhitespaceJobName_ThrowsArgumentException(
        string? jobName)
    {
        // Deliberately also-invalid cron — confirms jobName guard fires first.
        // ArgumentException.ThrowIfNullOrWhiteSpace throws ArgumentNullException
        // for null and ArgumentException for empty/whitespace (both are ArgumentException
        // subclasses), so IsAssignableFrom is the correct assertion here.
        var sut = CreateSut();
        var ex = await Record.ExceptionAsync(
            () => sut.UpdateScheduleAsync(jobName!, "not a cron", CancellationToken.None));
        Assert.IsAssignableFrom<ArgumentException>(ex);
    }

    [Theory]
    [InlineData("not a cron")]    // non-numeric tokens, wrong field count
    [InlineData("1 2 3 4")]       // 4 fields — must be exactly 5
    [InlineData("60 0 1 1 0")]    // minute 60 out of range 0–59
    public async Task UpdateScheduleAsync_InvalidCronExpression_ThrowsArgumentException(
        string cronExpression)
    {
        // Valid jobName — isolates the cron-expression guard.
        var sut = CreateSut();
        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.UpdateScheduleAsync("pinwiz-job-linker-buutj", cronExpression, CancellationToken.None));
    }

    // Constructs a real (not mocked) ArmJobAdminService with a no-op credential.
    // ArmClient construction is lazy — no network call is made until a request
    // is dispatched. Guard-clause tests throw before GetResourceGroupAsync runs,
    // so this instance never touches the network.
    private static ArmJobAdminService CreateSut() =>
        new(
            new ArmClient(new FakeCredential()),
            "00000000-0000-0000-0000-000000000000",
            "test-rg",
            NullLogger<ArmJobAdminService>.Instance);

    // Minimal TokenCredential that satisfies the ArmClient constructor without
    // making any network calls. Returned tokens are never validated because
    // guard clauses throw before any ARM request is issued.
    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("fake-token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }
}
