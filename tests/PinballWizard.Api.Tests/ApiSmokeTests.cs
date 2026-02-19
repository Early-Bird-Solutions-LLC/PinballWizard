using Xunit;

namespace PinballWizard.Api.Tests;

public class ApiSmokeTests
{
    [Fact]
    public void ApiSettings_DefaultValues_AreValid()
    {
        var settings = new ApiSettings();

        Assert.Equal(12_000, settings.ContextTokenBudget);
        Assert.Equal(10, settings.MaxConversationTurns);
        Assert.Equal(24, settings.JwtExpiryHours);
    }
}
