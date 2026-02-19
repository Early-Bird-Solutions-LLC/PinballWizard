namespace PinballWizard.Processor;

public sealed class ProcessorSettings
{
    public string StorageConnectionString { get; set; } = string.Empty;
    public string SearchEndpoint { get; set; } = string.Empty;
    public string SearchIndexName { get; set; } = "pinball-chunks";
    public string DocumentIntelligenceEndpoint { get; set; } = string.Empty;
    public string SpeechEndpoint { get; set; } = string.Empty;
    public int ChunkTokenSize { get; set; } = 512;
    public int ChunkOverlap { get; set; } = 128;
    public int IndexBatchSize { get; set; } = 100;
}
