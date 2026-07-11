using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using PinballWizard.Web.Services;
using Xunit;

namespace PinballWizard.Web.Tests.Services;

// Unit tests for BuildInfo — build-time identity parsed from environment
// variables injected by the Dockerfile ARG → ENV chain at image build.
//
// All paths must degrade visibly (Invariant #17 no-masking-fallbacks):
//   - absent/empty SHA  → ShortSha = "local"
//   - absent/empty time → BuildTimeUtc = null
//   - garbage time      → BuildTimeUtc = null
// No fabricated version strings are ever returned.
public sealed class BuildInfoTests
{
    private static IHostEnvironment TestHostEnv(string name = "Testing")
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static IConfiguration BuildConfig(params (string Key, string Value)[] entries)
    {
        var dict = entries.ToDictionary(e => e.Key, e => (string?)e.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    // ── ShortSha ─────────────────────────────────────────────────────────────

    [Fact]
    public void ShortSha_IsTrimmedToSevenChars_WhenShaIsFullLength()
    {
        var config = BuildConfig(("PINWIZ_BUILD_SHA", "abc1234def5678901234"));
        var info = new BuildInfo(config, TestHostEnv());
        Assert.Equal("abc1234", info.ShortSha);
    }

    [Fact]
    public void ShortSha_IsReturnedAsIs_WhenShaIsShorterThanSeven()
    {
        var config = BuildConfig(("PINWIZ_BUILD_SHA", "abc12"));
        var info = new BuildInfo(config, TestHostEnv());
        Assert.Equal("abc12", info.ShortSha);
    }

    [Fact]
    public void ShortSha_IsExactlySevenChars_WhenShaIsExactlySevenChars()
    {
        var config = BuildConfig(("PINWIZ_BUILD_SHA", "abc1234"));
        var info = new BuildInfo(config, TestHostEnv());
        Assert.Equal("abc1234", info.ShortSha);
    }

    [Fact]
    public void ShortSha_IsLocal_WhenShaIsAbsent()
    {
        var config = BuildConfig(); // no PINWIZ_BUILD_SHA key
        var info = new BuildInfo(config, TestHostEnv());
        Assert.Equal("local", info.ShortSha);
    }

    [Fact]
    public void ShortSha_IsLocal_WhenShaIsEmpty()
    {
        var config = BuildConfig(("PINWIZ_BUILD_SHA", ""));
        var info = new BuildInfo(config, TestHostEnv());
        Assert.Equal("local", info.ShortSha);
    }

    [Fact]
    public void ShortSha_IsLocal_WhenShaIsWhitespace()
    {
        var config = BuildConfig(("PINWIZ_BUILD_SHA", "   "));
        var info = new BuildInfo(config, TestHostEnv());
        Assert.Equal("local", info.ShortSha);
    }

    // ── BuildTimeUtc ──────────────────────────────────────────────────────────

    [Fact]
    public void BuildTimeUtc_IsParsed_WhenTimeIsValidIso8601()
    {
        var config = BuildConfig(
            ("PINWIZ_BUILD_SHA", "abc1234"),
            ("PINWIZ_BUILD_TIME", "2026-07-10T12:30:00Z"));
        var info = new BuildInfo(config, TestHostEnv());
        Assert.NotNull(info.BuildTimeUtc);
        Assert.Equal(2026, info.BuildTimeUtc!.Value.Year);
        Assert.Equal(7,    info.BuildTimeUtc.Value.Month);
        Assert.Equal(10,   info.BuildTimeUtc.Value.Day);
        Assert.Equal(12,   info.BuildTimeUtc.Value.Hour);
        Assert.Equal(30,   info.BuildTimeUtc.Value.Minute);
    }

    [Fact]
    public void BuildTimeUtc_IsNull_WhenTimeIsAbsent()
    {
        var config = BuildConfig(("PINWIZ_BUILD_SHA", "abc1234")); // no time key
        var info = new BuildInfo(config, TestHostEnv());
        Assert.Null(info.BuildTimeUtc);
    }

    [Fact]
    public void BuildTimeUtc_IsNull_WhenTimeIsEmpty()
    {
        var config = BuildConfig(
            ("PINWIZ_BUILD_SHA", "abc1234"),
            ("PINWIZ_BUILD_TIME", ""));
        var info = new BuildInfo(config, TestHostEnv());
        Assert.Null(info.BuildTimeUtc);
    }

    [Fact]
    public void BuildTimeUtc_IsNull_WhenTimeIsGarbage()
    {
        var config = BuildConfig(
            ("PINWIZ_BUILD_SHA", "abc1234"),
            ("PINWIZ_BUILD_TIME", "not-a-date"));
        var info = new BuildInfo(config, TestHostEnv());
        Assert.Null(info.BuildTimeUtc);
    }

    [Fact]
    public void BuildTimeUtc_IsNull_WhenTimeIsWhitespace()
    {
        var config = BuildConfig(
            ("PINWIZ_BUILD_SHA", "abc1234"),
            ("PINWIZ_BUILD_TIME", "   "));
        var info = new BuildInfo(config, TestHostEnv());
        Assert.Null(info.BuildTimeUtc);
    }

    // ── Environment ───────────────────────────────────────────────────────────

    [Fact]
    public void Environment_ReflectsHostEnvironmentName()
    {
        var config = BuildConfig();
        var info = new BuildInfo(config, TestHostEnv("Staging"));
        Assert.Equal("Staging", info.Environment);
    }

    [Fact]
    public void Environment_IsProduction_WhenHostEnvIsProduction()
    {
        var config = BuildConfig();
        var info = new BuildInfo(config, TestHostEnv("Production"));
        Assert.Equal("Production", info.Environment);
    }

    // ── Local-dev degradation (both vars absent) ──────────────────────────────

    [Fact]
    public void LocalDev_ShowsLocalSha_AndNullTime_WhenNoEnvVarsPresent()
    {
        // This is the all-absent local-dev path — both vars unset.
        // No synthetic version must be fabricated (Invariant #17).
        var config = BuildConfig();
        var info = new BuildInfo(config, TestHostEnv("Development"));

        Assert.Equal("local", info.ShortSha);
        Assert.Null(info.BuildTimeUtc);
        Assert.Equal("Development", info.Environment);
    }
}
