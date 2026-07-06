using System.Text.Json;

namespace PinballWizard.Web.Engineering;

public sealed record EngineeringManifestEntry(string Slug, string SourcePath, string Title, string Group, int Order);

public static class EngineeringManifest
{
    public const string ManifestRelativePath = "docs/engineering-manifest.json";

    public static IReadOnlyList<EngineeringManifestEntry> Load(string repoRoot)
    {
        var path = Path.Combine(repoRoot, ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var list = new List<EngineeringManifestEntry>();
        foreach (var e in doc.RootElement.GetProperty("docs").EnumerateArray())
        {
            list.Add(new EngineeringManifestEntry(
                e.GetProperty("slug").GetString()!,
                e.GetProperty("sourcePath").GetString()!,
                e.GetProperty("title").GetString()!,
                e.GetProperty("group").GetString()!,
                e.GetProperty("order").GetInt32()));
        }
        return list;
    }
}
