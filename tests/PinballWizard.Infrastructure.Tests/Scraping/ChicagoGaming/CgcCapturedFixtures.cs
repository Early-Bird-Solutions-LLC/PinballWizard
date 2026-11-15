namespace PinballWizard.Infrastructure.Tests.Scraping.ChicagoGaming;

/// <summary>
/// Loads the live-captured Chicago Gaming pages under
/// <c>Fixtures/Cgc/</c>. See that directory's CAPTURE.md.
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

    public static string Home() => Read("homepage.captured.html");

    public static string Sitemap() => Read("sitemap.captured.xml");

    public static string MachinePage(string slug)
    {
        if (!MachineSlugs.Contains(slug, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(slug), slug, "No captured page for this slug.");
        }

        return Read($"coinop-{slug}.captured.html");
    }

    public static string Read(string fileName) => File.ReadAllText(PathOf(fileName));

    public static string PathOf(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var segment = Path.IsPathRooted(fileName) ? Path.GetFileName(fileName) : fileName;
        if (string.IsNullOrEmpty(segment)
            || segment.Contains("..", StringComparison.Ordinal)
            || segment.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || segment.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Fixture file name must be a single relative segment: {fileName}");
        }

        var root = Path.GetFullPath(Directory()).TrimEnd(Path.DirectorySeparatorChar);
        return root + Path.DirectorySeparatorChar + segment;
    }

    public static string Directory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(JoinRelative(dir.FullName, "PinballWizard.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root from the test assembly.");
        }

        return JoinRelative(
            dir.FullName,
            "tests",
            "PinballWizard.Infrastructure.Tests",
            "Fixtures",
            "Cgc");
    }

    private static string JoinRelative(string directory, params ReadOnlySpan<string> segments)
    {
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment)
                || Path.IsPathRooted(segment)
                || segment.Contains("..", StringComparison.Ordinal)
                || segment.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || segment.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Path segment must be a single relative name: {segment}");
            }
        }

        string[] parts = [directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), .. segments];
        return string.Join(Path.DirectorySeparatorChar, parts);
    }
}
