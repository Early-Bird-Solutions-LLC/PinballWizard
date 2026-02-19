using Xunit;

namespace PinballWizard.Processor.Tests;

public class ProcessorSmokeTests
{
    [Fact]
    public void ProcessorSettings_DefaultValues_AreValid()
    {
        var settings = new ProcessorSettings();

        Assert.Equal(512, settings.ChunkTokenSize);
        Assert.Equal(128, settings.ChunkOverlap);
        Assert.Equal(100, settings.IndexBatchSize);
    }
}
