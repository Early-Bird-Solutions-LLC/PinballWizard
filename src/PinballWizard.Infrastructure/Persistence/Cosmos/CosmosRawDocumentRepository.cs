using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using PinballWizard.Application.Persistence;
using PinballWizard.Core.Models;

namespace PinballWizard.Infrastructure.Persistence.Cosmos;

// Cosmos-backed IRawDocumentRepository.
//
// Writes into the `scraped_documents_raw` container. Each record
// represents a unique file URL; partition key = document_id, id = document_id.
//
// UpsertRawAsync is idempotent: on re-discovery it preserves all
// linker-managed fields (link_status, resolution_strategy, etc.) and
// only touches timeline.last_checked_at + cross_references.
internal sealed class CosmosRawDocumentRepository
    : CosmosRepository<RawDocumentCosmosRecord>, IRawDocumentRepository
{
    public CosmosRawDocumentRepository(Container container, ILogger<CosmosRawDocumentRepository> logger)
        : base(container, logger)
    {
    }

    // IRawDocumentRepository.UpsertRawAsync
    public async Task<RawDocumentRecord> UpsertRawAsync(
        DocumentRecord record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var existing = await GetByIdAsync(record.DocumentId, record.DocumentId, cancellationToken)
            .ConfigureAwait(false);

        RawDocumentCosmosRecord cosmos;
        if (existing is not null)
        {
            // Preserve all linker-managed state; update only what the
            // scraper owns: timeline.last_checked_at and cross-references.
            existing.Timeline ??= new RawTimelineInfo
            {
                FirstDiscoveredAt = record.Timeline.FirstDiscoveredAt,
            };
            existing.Timeline.LastCheckedAt = DateTime.UtcNow;

            // Merge new cross-references (deduplicate by AlsoFoundAt URL).
            var existingUrls = new HashSet<string>(
                existing.CrossReferences.Select(x => x.AlsoFoundAt),
                StringComparer.OrdinalIgnoreCase);

            foreach (var xref in record.CrossReferences)
            {
                if (existingUrls.Add(xref.AlsoFoundAt))
                {
                    existing.CrossReferences.Add(new RawCrossRef
                    {
                        AlsoFoundAt = xref.AlsoFoundAt,
                        DiscoveryContext = xref.DiscoveryContext,
                        LinkText = xref.LinkText,
                        DiscoveredAt = xref.DiscoveredAt,
                    });
                }
            }

            // Propagate download/content timestamps if the scraper produced new ones.
            if (record.Timeline.LastDownloadedAt.HasValue)
            {
                existing.Timeline.LastDownloadedAt = record.Timeline.LastDownloadedAt;
            }

            if (record.Timeline.LastContentChangedAt.HasValue)
            {
                existing.Timeline.LastContentChangedAt = record.Timeline.LastContentChangedAt;
            }

            // Update content_hash if the scraper produced a new one.
            if (record.File?.Sha256 is { } newHash && !string.IsNullOrWhiteSpace(newHash))
            {
                existing.ContentHash = newHash;
            }

            cosmos = existing;
        }
        else
        {
            cosmos = MapToCosmosRecord(record);
        }

        await base.UpsertAsync(cosmos, cancellationToken).ConfigureAwait(false);
        return MapToDomain(cosmos);
    }

    // IRawDocumentRepository.StreamByStatusAsync
    public async IAsyncEnumerable<RawDocumentRecord> StreamByStatusAsync(
        IReadOnlyCollection<LinkStatus> statuses,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (statuses.Count == 0)
            yield break;

        // Build parameterised IN clause: WHERE c.link_status IN (@s0, @s1, ...)
        var paramNames = statuses.Select((_, i) => $"s{i}").ToList();
        var inClause = string.Join(", ", paramNames.Select(n => $"@{n}"));
        var query = $"SELECT * FROM c WHERE c.link_status IN ({inClause})";

        var parameters = new Dictionary<string, object>();
        var statusList = statuses.ToList();
        for (var i = 0; i < statusList.Count; i++)
        {
            parameters[$"s{i}"] = ToWireStatus(statusList[i]);
        }

        await foreach (var cosmos in StreamCrossPartitionAsync(query, parameters, cancellationToken).ConfigureAwait(false))
        {
            yield return MapToDomain(cosmos);
        }
    }

    // IRawDocumentRepository.StreamAllAsync
    public async IAsyncEnumerable<RawDocumentRecord> StreamAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var cosmos in StreamCrossPartitionAsync("SELECT * FROM c", parameters: null, cancellationToken).ConfigureAwait(false))
        {
            yield return MapToDomain(cosmos);
        }
    }

    // IRawDocumentRepository.UpdateFileAsync
    public async Task UpdateFileAsync(
        string documentId,
        DownloadedFileInfo file,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(file);

        var existing = await GetByIdAsync(documentId, documentId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            throw new InvalidOperationException(
                $"UpdateFileAsync: document {documentId} not found in scraped_documents_raw.");
        }

        existing.File = new RawFileInfo
        {
            LocalPath = file.LocalPath,
            Filename = file.Filename,
            SizeBytes = file.SizeBytes,
            Sha256 = file.Sha256,
            MimeType = file.MimeType,
            PageCount = file.PageCount,
        };
        existing.Timeline ??= new RawTimelineInfo { FirstDiscoveredAt = DateTime.UtcNow };
        existing.Timeline.LastDownloadedAt = DateTime.UtcNow;

        await base.UpsertAsync(existing, cancellationToken).ConfigureAwait(false);
    }

    // IRawDocumentRepository.UpdateLinkStatusAsync
    public async Task UpdateLinkStatusAsync(
        string documentId,
        LinkStatus status,
        string? resolutionStrategy,
        string? failureReason,
        string? overrideId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        var existing = await GetByIdAsync(documentId, documentId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            // A missing raw record means the scraped_documents write already went through
            // but the raw record was never created, or the record was deleted out of band.
            // Either way the caller's status stamp would be silently lost — throw so the
            // caller can decide whether to treat this as Failed and surface it in the admin UI.
            throw new InvalidOperationException(
                $"UpdateLinkStatusAsync: document {documentId} not found in scraped_documents_raw.");
        }

        existing.LinkStatus = ToWireStatus(status);
        existing.ResolutionStrategy = resolutionStrategy;
        existing.LinkFailureReason = failureReason;
        existing.OverrideId = overrideId;
        existing.LinkAttemptedAt = DateTimeOffset.UtcNow;

        // Stamp LinkedAt on terminal resolution. LinkedBy stays null for
        // automated linking; the admin UI sets it directly when a human links.
        if (status is LinkStatus.Linked or LinkStatus.ManuallyLinked or LinkStatus.PlatformGeneric)
        {
            existing.LinkedAt = DateTimeOffset.UtcNow;
        }

        await base.UpsertAsync(existing, cancellationToken).ConfigureAwait(false);
    }

    // IRawDocumentRepository.GetAsync
    public async Task<RawDocumentRecord?> GetAsync(
        string documentId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        var cosmos = await GetByIdAsync(documentId, documentId, cancellationToken)
            .ConfigureAwait(false);

        return cosmos is null ? null : MapToDomain(cosmos);
    }

    // IRawDocumentRepository.StreamBySourcePatternAsync
    // sourcePattern is either a plain URL prefix (e.g. "https://sternpinball.com/support")
    // or a pipe-delimited composite key produced by LinkOverrideRecord.BuildSourcePattern:
    // "{discoveryUrl}|{documentType}". The pipe form queries both fields independently;
    // the plain form falls back to the original OR-based CONTAINS query.
    public async IAsyncEnumerable<RawDocumentRecord> StreamBySourcePatternAsync(
        string sourcePattern,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePattern);

        string query;
        Dictionary<string, object> parameters;

        var pipeIdx = sourcePattern.IndexOf('|');
        if (pipeIdx >= 0)
        {
            var urlPart = sourcePattern[..pipeIdx];
            var typePart = sourcePattern[(pipeIdx + 1)..];
            query =
                "SELECT * FROM c WHERE " +
                "CONTAINS(c.source.discovery_url, @urlPart, true) AND " +
                "c.document_type = @typePart";
            parameters = new Dictionary<string, object>
            {
                ["urlPart"] = urlPart,
                ["typePart"] = typePart,
            };
        }
        else
        {
            query =
                "SELECT * FROM c WHERE " +
                "CONTAINS(c.source.discovery_url, @pattern, true) OR " +
                "c.document_type = @pattern";
            parameters = new Dictionary<string, object>
            {
                ["pattern"] = sourcePattern,
            };
        }

        await foreach (var cosmos in StreamCrossPartitionAsync(query, parameters, cancellationToken).ConfigureAwait(false))
        {
            yield return MapToDomain(cosmos);
        }
    }

    // ── Mapping helpers ──────────────────────────────────────────────────────

    private static RawDocumentCosmosRecord MapToCosmosRecord(DocumentRecord record)
    {
        return new RawDocumentCosmosRecord
        {
            Id = record.DocumentId,
            PartitionKey = record.DocumentId,
            DocumentUrl = record.Source?.FileUrl ?? string.Empty,
            DocumentType = record.Classification.DocumentType.ToString(),
            ContentHash = record.File?.Sha256,
            LinkStatus = "pending",
            Source = record.Source is { } src
                ? new RawSourceInfo
                {
                    DiscoveryUrl = src.DiscoveryUrl,
                    DiscoveryContext = src.DiscoveryContext,
                    FileUrl = src.FileUrl,
                    LinkText = src.LinkText,
                    SourceType = src.SourceType.ToString(),
                    ActionType = src.ActionType.ToString(),
                    Tab = src.Tab,
                    ScrapedAt = src.ScrapedAt,
                }
                : null,
            Classification = new RawClassificationInfo
            {
                DocumentType = record.Classification.DocumentType.ToString(),
                FileFormat = record.Classification.FileFormat,
            },
            File = record.File is { } file
                ? new RawFileInfo
                {
                    LocalPath = file.LocalPath,
                    Filename = file.Filename,
                    SizeBytes = file.SizeBytes,
                    Sha256 = file.Sha256,
                    MimeType = file.MimeType,
                    PageCount = file.PageCount,
                }
                : null,
            Http = record.Http is { } http
                ? new RawHttpInfo
                {
                    ETag = http.ETag,
                    LastModified = http.LastModified,
                    ContentType = http.ContentType,
                    ContentLength = http.ContentLength,
                }
                : null,
            Timeline = new RawTimelineInfo
            {
                FirstDiscoveredAt = record.Timeline.FirstDiscoveredAt,
                LastCheckedAt = record.Timeline.LastCheckedAt,
                LastDownloadedAt = record.Timeline.LastDownloadedAt,
                LastContentChangedAt = record.Timeline.LastContentChangedAt,
                VersionCount = record.Timeline.VersionCount,
            },
            CrossReferences = record.CrossReferences
                .Select(x => new RawCrossRef
                {
                    AlsoFoundAt = x.AlsoFoundAt,
                    DiscoveryContext = x.DiscoveryContext,
                    LinkText = x.LinkText,
                    DiscoveredAt = x.DiscoveredAt,
                })
                .ToList(),
            RunId = record.RunId,
        };
    }

    private RawDocumentRecord MapToDomain(RawDocumentCosmosRecord cosmos)
    {
        var pascalStatus = ToPascalStatus(cosmos.LinkStatus);
        if (!Enum.TryParse<LinkStatus>(pascalStatus, out var linkStatus))
        {
            Logger.LogWarning(
                "MapToDomain: unrecognised link_status wire value '{WireStatus}' for doc {DocId} — treating as Pending.",
                cosmos.LinkStatus, cosmos.PartitionKey);
            linkStatus = LinkStatus.Pending;
        }

        if (!Enum.TryParse<DocumentType>(cosmos.DocumentType, out var documentType))
        {
            Logger.LogWarning(
                "MapToDomain: unrecognised document_type wire value '{WireType}' for doc {DocId} — treating as Other.",
                cosmos.DocumentType, cosmos.PartitionKey);
            documentType = DocumentType.Other;
        }

        return new RawDocumentRecord
        {
            DocumentId = cosmos.PartitionKey,
            DocumentUrl = cosmos.DocumentUrl,
            DocumentType = documentType,
            ContentHash = cosmos.ContentHash,
            LinkStatus = linkStatus,
            ResolutionStrategy = cosmos.ResolutionStrategy,
            LinkAttemptedAt = cosmos.LinkAttemptedAt,
            LinkFailureReason = cosmos.LinkFailureReason,
            LinkedBy = cosmos.LinkedBy,
            LinkedAt = cosmos.LinkedAt,
            OverrideId = cosmos.OverrideId,
            LinkedMachineIds = cosmos.LinkedMachineIds,
            Source = cosmos.Source is { } src
                ? new SourceInfo
                {
                    DiscoveryUrl = src.DiscoveryUrl,
                    DiscoveryContext = src.DiscoveryContext,
                    FileUrl = src.FileUrl,
                    LinkText = src.LinkText,
                    ActionType = Enum.TryParse<ActionType>(src.ActionType, out var at) ? at : default,
                    SourceType = Enum.TryParse<SourceType>(src.SourceType, out var st) ? st : default,
                    Tab = src.Tab,
                    ScrapedAt = src.ScrapedAt,
                }
                : new SourceInfo
                {
                    DiscoveryUrl = cosmos.DocumentUrl,
                    DiscoveryContext = string.Empty,
                    FileUrl = cosmos.DocumentUrl,
                },
            Classification = cosmos.Classification is { } cls
                ? new ClassificationInfo
                {
                    DocumentType = Enum.TryParse<DocumentType>(cls.DocumentType, out var dt) ? dt : documentType,
                    FileFormat = cls.FileFormat,
                }
                : new ClassificationInfo
                {
                    DocumentType = documentType,
                    FileFormat = string.Empty,
                },
            File = cosmos.File is { } f
                ? new DownloadedFileInfo
                {
                    LocalPath = f.LocalPath,
                    Filename = f.Filename,
                    SizeBytes = f.SizeBytes,
                    Sha256 = f.Sha256,
                    MimeType = f.MimeType,
                    PageCount = f.PageCount,
                }
                : null,
            Http = cosmos.Http is { } h
                ? new HttpMetadata
                {
                    ETag = h.ETag,
                    LastModified = h.LastModified,
                    ContentType = h.ContentType,
                    ContentLength = h.ContentLength,
                }
                : null,
            Timeline = cosmos.Timeline is { } tl
                ? new TimelineInfo
                {
                    FirstDiscoveredAt = tl.FirstDiscoveredAt,
                    LastCheckedAt = tl.LastCheckedAt,
                    LastDownloadedAt = tl.LastDownloadedAt,
                    LastContentChangedAt = tl.LastContentChangedAt,
                    VersionCount = tl.VersionCount,
                }
                : new TimelineInfo
                {
                    FirstDiscoveredAt = DateTime.UtcNow,
                },
            CrossReferences = cosmos.CrossReferences
                .Select(x => new CrossReference
                {
                    AlsoFoundAt = x.AlsoFoundAt,
                    DiscoveryContext = x.DiscoveryContext,
                    LinkText = x.LinkText,
                    DiscoveredAt = x.DiscoveredAt,
                })
                .ToList(),
            RunId = cosmos.RunId,
        };
    }

    // Convert LinkStatus enum to the snake_case wire string stored in Cosmos.
    private static string ToWireStatus(LinkStatus status) => status switch
    {
        LinkStatus.Pending => "pending",
        LinkStatus.Linked => "linked",
        LinkStatus.PlatformGeneric => "platform_generic",
        LinkStatus.NotInCatalog => "not_in_catalog",
        LinkStatus.Failed => "failed",
        LinkStatus.ManuallyLinked => "manually_linked",
        _ => throw new InvalidOperationException($"Unhandled LinkStatus value: {status}"),
    };

    // Convert wire snake_case string back to PascalCase for Enum.TryParse.
    // Unrecognised values return an empty string so TryParse fails and the caller
    // can log a warning and choose a safe default (see MapToDomain).
    private static string ToPascalStatus(string wireStatus) => wireStatus switch
    {
        "pending" => "Pending",
        "linked" => "Linked",
        "platform_generic" => "PlatformGeneric",
        "not_in_catalog" => "NotInCatalog",
        "failed" => "Failed",
        "manually_linked" => "ManuallyLinked",
        _ => string.Empty,
    };
}
