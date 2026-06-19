using Xunit;

namespace PinballWizard.Infrastructure.Tests.Documents;

// Skips a test unless AZURITE_BLOB_SERVICE_URL is set to a non-empty
// value, making xUnit report it as Skipped rather than Passed when
// Azurite is not available. Mirrors the E2EFactAttribute pattern in
// PinballWizard.Web.Tests.
//
// Set AZURITE_BLOB_SERVICE_URL to the Azurite blob service endpoint
// (e.g. http://127.0.0.1:10000/devstoreaccount1 for a local instance,
// or the Aspire-managed URL) to run the guarded tests for real.
public sealed class RequiresAzuriteFactAttribute : FactAttribute
{
    internal const string EnvVar = "AZURITE_BLOB_SERVICE_URL";

    public RequiresAzuriteFactAttribute()
    {
        var value = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            Skip = $"{EnvVar} is not set — start Azurite and set the variable to run these tests.";
        }
    }
}
