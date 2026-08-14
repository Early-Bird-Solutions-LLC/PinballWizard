using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PinballWizard.Application.Documents;
using PinballWizard.Application.Linking;
using PinballWizard.Application.Observability;
using PinballWizard.Application.Persistence;
using PinballWizard.Application.Resolution;
using PinballWizard.Core.Domain;
using PinballWizard.Core.Models;
using Xunit;

namespace PinballWizard.Application.Tests.Linking;

// The linker's instruments must hang off the SHARED PinballWizardTelemetry.Meter.
//
// ServiceDefaults subscribes the MeterProvider with AddMeter("PinballWizard") — an EXACT
// name match. DocumentLinker used to own `new Meter("PinballWizard.Linking")`, which that
// subscription never matched, so every pinwiz.linker.* measurement was discarded before
// reaching an exporter: silently, at zero runtime cost, with no error anywhere. #840 stayed
// open partly because of it — the exporter wiring, env var, job image and Entra credential
// were all eventually correct and these counters still did not exist as far as App Insights
// was concerned.
//
// The listener below filters on exactly the name ServiceDefaults registers, so it fails the
// same way the exporter did. An instrument parked on an unregistered meter fails here
// instead of vanishing in production.
public sealed class DocumentLinkerMeterScopeTests : IDisposable
{
    private readonly MeterListener _listener;
    private readonly ConcurrentBag<string> _observed = [];

    public DocumentLinkerMeterScopeTests()
    {
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            // Load-bearing: subscribe ONLY to the meter ServiceDefaults registers.
            if (instrument.Meter.Name == PinballWizardTelemetry.MeterName)
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instr, _, _, _) => _observed.Add(instr.Name));
        _listener.SetMeasurementEventCallback<double>((instr, _, _, _) => _observed.Add(instr.Name));
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task LinkerInstruments_AreObservableOnTheRegisteredMeter()
    {
        var rawRepo = Substitute.For<IRawDocumentRepository>();
        var overrideRepo = Substitute.For<ILinkOverrideRepository>();
        var machineRepo = Substitute.For<IMachineRepository>();
        var docWriter = Substitute.For<IScrapedDocumentRepository>();
        var aliasLoader = Substitute.For<IMachineAliasLoader>();

        overrideRepo.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, LinkOverrideRecord>());
        machineRepo.StreamAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Machine>().ToAsyncEnumerable());
        aliasLoader.LoadAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MachineAliasEntry>());

        // One unlinkable document is enough: the per-document counter and the
        // per-run histogram both fire regardless of the linking outcome.
        var raw = new RawDocumentRecord
        {
            DocumentId = "doc_meter_scope_001",
            DocumentUrl = "https://example.com/files/unknown_thing.pdf",
            DocumentType = DocumentType.Manual,
            Source = new SourceInfo
            {
                DiscoveryUrl = "https://sternpinball.com/manuals/",
                DiscoveryContext = "Manuals page",
                FileUrl = "https://example.com/files/unknown_thing.pdf",
                ScrapedAt = DateTime.UtcNow,
                SourceType = SourceType.ManualsPage,
            },
            Timeline = new TimelineInfo { FirstDiscoveredAt = DateTime.UtcNow },
            CrossReferences = [],
        };

        rawRepo.StreamByStatusAsync(
                Arg.Any<IReadOnlyCollection<LinkStatus>>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<RawDocumentRecord> { raw }.ToAsyncEnumerable());

        using var linker = new DocumentLinker(
            rawRepo, overrideRepo, machineRepo, docWriter,
            previewExtractor: null, NullLogger<DocumentLinker>.Instance, aliasLoader,
            blobStore: null);

        await linker.InitializeAsync(CancellationToken.None);
        await linker.RunBatchAsync(CancellationToken.None);

        Assert.Contains("pinwiz.linker.documents_processed_total", _observed);
        Assert.Contains("pinwiz.linker.run_duration_ms", _observed);
    }
}
