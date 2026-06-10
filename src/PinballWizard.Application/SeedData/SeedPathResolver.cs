namespace PinballWizard.Application.SeedData;

// Resolves repo-relative seed paths ("data/seeds/*.json") to a path that
// exists, regardless of how the host was launched:
//
//   - Deployed container: WORKDIR /app + the Dockerfile copies data/seeds
//     → the relative path resolves against the working directory as-is.
//   - Repo-root invocation (CLI usage documented in CLAUDE.md): as-is.
//   - `dotnet run` / IDE launches where the working directory is the
//     project directory or bin output: walk up from AppContext.BaseDirectory
//     until the relative path exists (the same strategy the contract tests
//     use to pin the production manifests).
//
// Before this resolver, the loaders resolved strictly against the working
// directory; under local `dotnet run` the Api's landing endpoint threw
// FileNotFoundException on every call (2026-06-10), which kept the Web
// app's resilience circuit breaker permanently open and turned a missing
// file into dead Blazor circuits. Returns the original path when nothing
// matches so callers keep their existing, clearly-worded failure.
public static class SeedPathResolver
{
    public static string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath) || File.Exists(relativePath))
        {
            return relativePath;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent!;
        }

        return relativePath;
    }
}
