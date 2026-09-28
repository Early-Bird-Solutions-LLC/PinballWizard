namespace PinballWizard.Infrastructure.Tests.Scraping.ChicagoGaming;

/// <summary>
/// Loads the live-captured Chicago Gaming pages under
/// <c>Fixtures/ChicagoGaming/</c>. See that directory's CAPTURE.md.
/// </summary>
internal static class CgcCapturedFixtures
{
    public const string BaseUrl = "https://www.chicago-gaming.com";

    public static readonly string[] MachineSlugs =
    [
        "attack-from-mars",
        "cactus-canyon",
        "medieval-madness",
        "monster-bash",
        "pulp-fiction",
    ];

    public static string Home() => Read("home.captured.html");

    public static string Sitemap() => Read("sitemap.captured.xml");

    public static string MachinePage(string slug)
    {
        if (!MachineSlugs.Contains(slug, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(slug), slug, "No captured page for this slug.");
        }

        return Read($"coinop-{slug}.captured.html");
    }

    private static string Read(string fileName) => File.ReadAllText(Path.Join(FixtureDir(), fileName));

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Join(dir.FullName, "PinballWizard.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root from the test assembly.");
        }

        return Path.Join(dir.FullName, "tests", "PinballWizard.Infrastructure.Tests", "Fixtures", "ChicagoGaming");
    }
}
