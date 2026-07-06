using PinballWizard.Web.Engineering;
using Xunit;

namespace PinballWizard.Web.Tests.Engineering;

public sealed class EngineeringManifestConformanceTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PinballWizard.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root by walking up from " +
            $"AppContext.BaseDirectory ({AppContext.BaseDirectory}). " +
            "Expected to find 'PinballWizard.slnx' in an ancestor directory.");
    }

    [Fact]
    public void EveryManifestEntry_ResolvesToAnExistingSourceFile()
    {
        var root = RepoRoot();
        var entries = EngineeringManifest.Load(root);
        Assert.NotEmpty(entries);
        foreach (var e in entries)
        {
            var full = Path.Combine(root, e.SourcePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(full), $"Manifest slug '{e.Slug}' points at missing file '{e.SourcePath}'.");
        }
    }

    [Fact]
    public void EverySlug_IsUniqueAndUrlSafe()
    {
        var entries = EngineeringManifest.Load(RepoRoot());
        Assert.Equal(entries.Select(e => e.Slug).Distinct().Count(), entries.Count);
        Assert.All(entries, e => Assert.Matches("^[a-z0-9-]+$", e.Slug));
    }
}
