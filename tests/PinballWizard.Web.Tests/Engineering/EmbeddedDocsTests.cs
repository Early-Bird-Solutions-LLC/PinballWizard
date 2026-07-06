using System.Reflection;
using Xunit;

namespace PinballWizard.Web.Tests.Engineering;

public sealed class EmbeddedDocsTests
{
    [Fact]
    public void WebAssembly_EmbedsManifestListedDocs()
    {
        var asm = typeof(PinballWizard.Web.Engineering.EngineeringManifest).Assembly;
        var names = asm.GetManifestResourceNames();
        Assert.Contains(names, n => n.EndsWith("vision.md", StringComparison.Ordinal));
        Assert.Contains(names, n => n.Contains("adr", StringComparison.OrdinalIgnoreCase)
                                    && n.EndsWith(".md", StringComparison.Ordinal));
    }

    [Fact]
    public void WebAssembly_CarriesEngineeringSourceCommitMetadata()
    {
        var asm = typeof(PinballWizard.Web.Engineering.EngineeringManifest).Assembly;
        var meta = asm.GetCustomAttributes<AssemblyMetadataAttribute>();
        Assert.Contains(meta, m => m.Key == "EngineeringSourceCommit");
        Assert.Contains(meta, m => m.Key == "EngineeringBuildDate");
    }
}
