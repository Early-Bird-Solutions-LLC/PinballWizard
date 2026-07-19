using Xunit;

namespace PinballWizard.Application.Tests.Fixtures;

// Marks a test as Skipped (rather than silently Passed) when the specified
// fixture file has not yet been captured by the operator. The check runs at
// test-discovery time so that the runner always reports a concrete Skipped
// status — never a false-green — when the capture is pending.
//
// Mirrors the RequiresAzuriteFactAttribute / E2EFactAttribute pattern used
// elsewhere in this test suite.
//
// Usage:
//   [RequiresCapturedFixtureFact(
//       "tests/PinballWizard.Application.Tests/Fixtures/Linking/golden-link-set.captured.json",
//       "Run: dotnet run --project src/PinballWizard.Cli -- --capture-golden-set")]
//   public async Task LiveFixture_Replays_...() { ... }
internal sealed class RequiresCapturedFixtureFactAttribute : FactAttribute
{
    public RequiresCapturedFixtureFactAttribute(
        string repoRelativeFixturePath,
        string captureHint)
    {
        // Walk up from the test binary directory to find the repo root
        // (identified by PinballWizard.slnx). If the walk fails, we let
        // the test run and fail naturally with a clear FileNotFoundException.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
            dir = dir.Parent;

        if (dir is null)
            return;

        var fullPath = Path.Combine(dir.FullName, repoRelativeFixturePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
            Skip = $"Live fixture not yet captured. {captureHint}. Expected at: {fullPath}";
    }
}
