namespace PinballWizard.Infrastructure.Tests.Scraping.Ap;

// Resolves files under tests/PinballWizard.Infrastructure.Tests/Fixtures/Ap —
// the captured live AP responses (see CAPTURE.md in that directory).
internal static class ApFixtures
{
    public static string Read(string fileName) => File.ReadAllText(PathOf(fileName));

    public static string PathOf(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        // A rooted name is reduced to its file name so it cannot replace the fixture directory.
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
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
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
            "Ap");
    }

    private static string JoinRelative(string directory, params ReadOnlySpan<string> segments)
    {
        var path = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

            path += Path.DirectorySeparatorChar + segment;
        }

        return path;
    }
}
