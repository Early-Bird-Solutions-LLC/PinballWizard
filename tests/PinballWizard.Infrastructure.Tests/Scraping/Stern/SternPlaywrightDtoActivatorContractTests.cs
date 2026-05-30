using System.Reflection;
using System.Text.Json.Serialization;
using PinballWizard.Infrastructure.Scraping.Stern;
using Xunit;

// LinkRaw / BulletinRaw are internal DTO classes nested inside their
// scraper classes; aliases let the assertions read naturally.
using LinkRaw = PinballWizard.Infrastructure.Scraping.Stern.GamePageScraper.LinkRaw;
using BulletinRaw = PinballWizard.Infrastructure.Scraping.Stern.ServiceBulletinScraper.BulletinRaw;

namespace PinballWizard.Infrastructure.Tests.Scraping.Stern;

// Pins the deserialization contract Playwright actually invokes for the
// DTO types the Stern Playwright scrapers receive from page.EvaluateAsync.
//
// Playwright's EvaluateArgumentValueConverter.ToExpectedType (confirmed in
// 1.12.0 and again in 1.59.0 by live-site validation on 2026-05-04) calls
// Activator.CreateInstance(t) and then assigns each property from the
// parsed JSON. Three things must therefore hold for every DTO:
//
//   (1) Public parameterless constructor exists.
//   (2) Every property carrying a JsonPropertyName is publicly settable
//       (writable get/set, not init-only via positional record syntax).
//   (3) JsonPropertyName values match the JS-side keys.
//
// PR #72 attempted to convert these to positional records on the (incorrect)
// assumption that Playwright 1.59 had switched to System.Text.Json. The
// previous test file (SternPlaywrightRecordDeserializationTests) pinned STJ
// instead of Activator and so passed while the live path threw
// MissingMethodException. See docs/decision-log.md DL-0002.
//
// Stern Playwright scrapers have no automated integration tests
// (Phase 2 § Scope item 8 — route ii); this contract test is the only
// pre-merge surface that catches a regression on the deserialization shape.
public sealed class SternPlaywrightDtoActivatorContractTests
{
    // ── (1) Activator.CreateInstance + property-set round-trip ──────────

    [Theory]
    [InlineData(typeof(LinkRaw))]
    [InlineData(typeof(BulletinRaw))]
    public void DtoType_AllowsActivatorCreateThenPropertySet_AsPlaywrightRequires(Type dtoType)
    {
        // Mirrors EvaluateArgumentValueConverter.ToExpectedType's two-step
        // path: Activator.CreateInstance(t), then walk properties and
        // assign each one via SetValue. If either step throws, the live
        // scraper would fail with the exact MissingMethodException /
        // setter-violation seen on the 2026-05-04 live-site run.
        var instance = Activator.CreateInstance(dtoType);
        Assert.NotNull(instance);

        foreach (var prop in dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null))
        {
            var value = SampleValueFor(prop.PropertyType);
            prop.SetValue(instance, value);
            Assert.Equal(value, prop.GetValue(instance));
        }
    }

    // ── (1b) Pin the negative case: positional records would fail ───────

    [Fact]
    public void PositionalRecord_FailsActivatorCreateInstance_DocumentingWhyDtosAreClasses()
    {
        // Sentinel for DL-0002: if a future contributor reaches for a
        // positional record again ("records are more idiomatic"), this
        // test documents in code that Playwright's deserializer cannot
        // construct one. The exception bubbles up as the same
        // MissingMethodException PR #72's bulletins live-site run hit.
        Assert.Throws<MissingMethodException>(
            () => Activator.CreateInstance<PositionalRecordSentinel>());
    }

    private sealed record PositionalRecordSentinel(string Href);

    // ── (2) Every JsonPropertyName-bearing property is settable ─────────

    [Theory]
    [InlineData(typeof(LinkRaw))]
    [InlineData(typeof(BulletinRaw))]
    public void DtoType_AllJsonProperties_AreSettable(Type dtoType)
    {
        var properties = dtoType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
            .ToArray();

        Assert.NotEmpty(properties);

        foreach (var prop in properties)
        {
            // CanWrite catches both `init`-only setters (positional records)
            // and missing setters (read-only). Playwright's converter
            // assigns directly via the setter; init-only would throw at
            // runtime.
            Assert.True(
                prop.SetMethod is { IsPublic: true } && !IsInitOnly(prop),
                $"{dtoType.Name}.{prop.Name} must have a public, non-init setter.");
        }
    }

    // ── (3) JsonPropertyName values match the JS-side keys ──────────────

    [Fact]
    public void LinkRaw_JsonPropertyNames_MatchJsSideKeys()
    {
        // The JS push inside GamePageScraper.ExtractLinksAsync's
        // page.EvaluateAsync<LinkRaw[]?> emits these keys. Renaming a
        // property without updating the attribute (or vice versa) would
        // silently drop the field at deserialization.
        AssertJsonPropertyName<LinkRaw>(nameof(LinkRaw.Href), "href");
        AssertJsonPropertyName<LinkRaw>(nameof(LinkRaw.Text), "text");
        AssertJsonPropertyName<LinkRaw>(nameof(LinkRaw.IsDownload), "isDownload");
    }

    [Fact]
    public void BulletinRaw_JsonPropertyNames_MatchJsSideKeys()
    {
        // The JS push inside ServiceBulletinScraper.ExtractBulletinsAsync's
        // page.EvaluateAsync<BulletinRaw[]?> emits these keys.
        AssertJsonPropertyName<BulletinRaw>(nameof(BulletinRaw.Href), "href");
        AssertJsonPropertyName<BulletinRaw>(nameof(BulletinRaw.Text), "text");
        AssertJsonPropertyName<BulletinRaw>(nameof(BulletinRaw.Date), "date");
        AssertJsonPropertyName<BulletinRaw>(nameof(BulletinRaw.RelatedGames), "relatedGames");
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static bool IsInitOnly(PropertyInfo prop) =>
        prop.SetMethod?.ReturnParameter
            .GetRequiredCustomModifiers()
            .Any(t => t == typeof(System.Runtime.CompilerServices.IsExternalInit)) ?? false;

    private static void AssertJsonPropertyName<T>(string clrPropertyName, string expectedJsKey)
    {
        var prop = typeof(T).GetProperty(clrPropertyName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(prop);
        var attr = prop!.GetCustomAttribute<JsonPropertyNameAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(expectedJsKey, attr!.Name);
    }

    private static object? SampleValueFor(Type t)
    {
        if (t == typeof(string)) return "sample";
        if (t == typeof(bool)) return true;
        if (t == typeof(int)) return 1;
        return null;
    }
}
