using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PinballWizard.Application.Rag.Chunking;
using PinballWizard.Application.Rag.Indexing;
using PinballWizard.Core.Models;
using PinballWizard.Infrastructure.Scraping.P3Sdk;
using Xunit;

namespace PinballWizard.Infrastructure.Tests.Scraping.P3Sdk;

public sealed class P3SdkDocsSynthesizerTests : IDisposable
{
    // ── Arrange helpers ─────────────────────────────────────────────────────────

    private readonly IRagIndexer _ragIndexer;
    private readonly P3SdkDocsSynthesizer _sut;
    private readonly string _tempSdkDir;

    public P3SdkDocsSynthesizerTests()
    {
        _ragIndexer = Substitute.For<IRagIndexer>();

        // Default: UpsertAsync returns a success result (no failures).
        _ragIndexer
            .UpsertAsync(
                Arg.Any<ChunkRequest>(),
                Arg.Any<IReadOnlyList<Chunk>>(),
                Arg.Any<RagIndexerOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new IndexUpsertResult(Indexed: 1, Failures: []));

        var chunker = new HybridChunker(
            Options.Create(new ChunkerOptions()),
            NullLogger<HybridChunker>.Instance);

        _sut = new P3SdkDocsSynthesizer(
            chunker,
            _ragIndexer,
            NullLogger<P3SdkDocsSynthesizer>.Instance);

        // Build a minimal fake SDK directory that mirrors the real SDK layout.
        _tempSdkDir = Path.Join(Path.GetTempPath(), $"p3sdk_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempSdkDir);
        WriteFile("INSTALL.txt", "P3 SDK Installation\n\nUnzip to your Unity project.\nRequires Unity 2021.3 LTS or later.");
        WriteFile("ReleaseNotes.txt", "P3 SDK v0.9 Release Notes\n\nNew: Portal module driver updated.\nFixed: CCR timing issue resolved.");
        WriteModuleFile("CCR/2.3.1.1/UsageInstructions.txt", "CCR Usage Instructions\n\nThe CCR (Cannon Capture Ramp) driver controls the upper playfield capture mechanism.");
        WriteModuleFile("FR/1.0.4.7/UsageInstructions.txt", "FR Usage Instructions\n\nThe FR (Fight Ramp) driver integrates with the left ramp shot trigger.");
        WriteModuleFile("Portal/1.0.3.2/UsageInstructions.md", "# Portal Usage Instructions\n\nThe Portal driver manages the center playfield portal target.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempSdkDir))
            Directory.Delete(_tempSdkDir, recursive: true);
    }

    // ── Null guard tests ────────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullChunker_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new P3SdkDocsSynthesizer(null!, _ragIndexer, NullLogger<P3SdkDocsSynthesizer>.Instance));
    }

    [Fact]
    public void Ctor_NullRagIndexer_Throws()
    {
        var chunker = new HybridChunker(
            Options.Create(new ChunkerOptions()),
            NullLogger<HybridChunker>.Instance);

        Assert.Throws<ArgumentNullException>(() =>
            new P3SdkDocsSynthesizer(chunker, null!, NullLogger<P3SdkDocsSynthesizer>.Instance));
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var chunker = new HybridChunker(
            Options.Create(new ChunkerOptions()),
            NullLogger<HybridChunker>.Instance);

        Assert.Throws<ArgumentNullException>(() =>
            new P3SdkDocsSynthesizer(chunker, _ragIndexer, null!));
    }

    [Fact]
    public async Task SyncAsync_NullOrEmptySdkPath_Throws()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _sut.SyncAsync(null!));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _sut.SyncAsync(""));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            _sut.SyncAsync("   "));
    }

    [Fact]
    public async Task SyncAsync_NonExistentPath_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.SyncAsync(@"C:\does\not\exist\p3sdk.zip"));
    }

    // ── Happy-path tests ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SyncAsync_SdkDirectory_CallsUpsertOncePerPresentFile()
    {
        // The fake SDK has 5 files that match the high-value list.
        var indexed = await _sut.SyncAsync(_tempSdkDir);

        Assert.Equal(5, indexed);
        await _ragIndexer.Received(5).UpsertAsync(
            Arg.Any<ChunkRequest>(),
            Arg.Any<IReadOnlyList<Chunk>>(),
            Arg.Any<RagIndexerOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_SdkDirectory_UpsertReceivesCorrectDocumentType()
    {
        await _sut.SyncAsync(_tempSdkDir);

        await _ragIndexer.Received().UpsertAsync(
            Arg.Is<ChunkRequest>(r => r.DocumentType == DocumentType.SdkGuide),
            Arg.Any<IReadOnlyList<Chunk>>(),
            Arg.Any<RagIndexerOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_SdkDirectory_UpsertReceivesMultimorphicManufacturer()
    {
        await _sut.SyncAsync(_tempSdkDir);

        await _ragIndexer.Received().UpsertAsync(
            Arg.Is<ChunkRequest>(r => r.Manufacturer == "Multimorphic"),
            Arg.Any<IReadOnlyList<Chunk>>(),
            Arg.Any<RagIndexerOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_SdkDirectory_DocumentUrlContainsSdkBaseUrl()
    {
        await _sut.SyncAsync(_tempSdkDir);

        await _ragIndexer.Received().UpsertAsync(
            Arg.Is<ChunkRequest>(r => r.DocumentUrl.StartsWith("https://www.multimorphic.com/sdk/v0.9/", StringComparison.Ordinal)),
            Arg.Any<IReadOnlyList<Chunk>>(),
            Arg.Any<RagIndexerOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_SdkDirectory_DocumentIdHasP3SdkPrefix()
    {
        await _sut.SyncAsync(_tempSdkDir);

        await _ragIndexer.Received().UpsertAsync(
            Arg.Is<ChunkRequest>(r => r.DocumentId.StartsWith("p3sdk_", StringComparison.Ordinal)),
            Arg.Any<IReadOnlyList<Chunk>>(),
            Arg.Any<RagIndexerOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_MissingOptionalFile_SkipsAndIndexesRemaining()
    {
        // Remove two of the five fake files — synthesizer should gracefully skip them
        // and still index the others (no fabrication, no crash — Invariant #17).
        File.Delete(Path.Join(_tempSdkDir, "ReleaseNotes.txt"));
        File.Delete(Path.Join(_tempSdkDir, ".multimorphic", "P3", "ModuleDrivers", "FR", "1.0.4.7", "UsageInstructions.txt"));

        var indexed = await _sut.SyncAsync(_tempSdkDir);

        Assert.Equal(3, indexed);
    }

    [Fact]
    public async Task SyncAsync_EmptyFile_SkipsWithoutFabricating()
    {
        // Write an empty INSTALL.txt — the synthesizer must not fabricate content
        // and must not count it as indexed (Invariant #17).
        File.WriteAllText(Path.Join(_tempSdkDir, "INSTALL.txt"), string.Empty);

        var indexed = await _sut.SyncAsync(_tempSdkDir);

        // 4 remaining files (CCR, FR, Portal + ReleaseNotes still present).
        Assert.Equal(4, indexed);
    }

    // ── Helper methods ────────────────────────────────────────────────────────

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Join(_tempSdkDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private void WriteModuleFile(string moduleRelativePath, string content)
    {
        // Path is relative to .multimorphic/P3/ModuleDrivers/
        var fullRelative = Path.Join(".multimorphic", "P3", "ModuleDrivers", moduleRelativePath);
        WriteFile(fullRelative, content);
    }
}
