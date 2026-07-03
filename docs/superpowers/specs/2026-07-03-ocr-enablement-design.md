# OCR Enablement — activate the Azure Document Intelligence fallback

**Date:** 2026-07-03
**Status:** Design (approved to implement)
**Scope:** Infra config + RBAC + operational backfill. No application code.

## Problem

Scanned / image-only PDFs have no text layer, so PdfPig text extraction returns
near-zero text and the RAG ingestion handler skips them (`OcrRequired` → 0 chunks →
not indexed). Their content is therefore not searchable or citable by the Wizard.

Confirmed empirically (2026-07-03): 6 old Stern manuals — Avatar (2010), Mustang
(2014), NBA (2009), Transformers + Transformers LE (2011), X-Men (2012) — extract
**0 characters** via `pdftotext` (`/Font=0`, high `/Image` counts). They are linked to
their machines and appear on the Documents page, but their manual text is absent from
the AI Search index.

## Existing state (already built — just not enabled)

The OCR capability was scaffolded ("Phase 4.5 W1") and never turned on:

- `PdfPigDocumentTextExtractor` — primary text extraction; returns `OcrRequired` when a
  PDF has near-zero extractable text.
- `AzureDocumentIntelligenceExtractor` — ADI **Read** model OCR, `Azure.AI.DocumentIntelligence`
  1.0.0, auth via `DefaultAzureCredential`.
- `FallbackDocumentTextExtractor` — decorator: run PdfPig; on `OcrRequired`, delegate to
  the ADI extractor. Invisible to `IChunker` / `IRagIndexer`.
- `DocumentIntelligenceOptions` — binds `DocumentIntelligence:Endpoint`.
- Gated DI registration (`AddPdfDocumentTextExtractor(configuration)`): registers PdfPig
  alone, OR PdfPig + the ADI fallback **only when `DocumentIntelligence:Endpoint` is
  present**; otherwise PdfPig-only (image PDFs skipped, current behaviour). **Both** the
  RAG worker (`RagIngestionWorker/Program.cs:48`) and the CLI backfill path
  (`Cli/Program.cs:1596`) already call this — so setting the endpoint env var activates
  OCR in both with **zero code change** (confirmed 2026-07-03).
- Infra: `pinwiz-docint-dev-buutj` (kind `FormRecognizer`) is **live**, declared in
  `infra/modules/shared.bicep` gated on `deployPhase2`; `documentIntelligenceEndpoint`
  is already an output.

## Gaps (the entire remaining work)

1. **Config not wired** — `DocumentIntelligence__Endpoint` is not set on the RAG worker
   (`ragIndexerApp`) container-app env, so `FallbackDocumentTextExtractor` never
   registers in the deployed worker.
2. **RBAC missing** — the `ragIndexerApp` system-assigned managed identity has no
   data-plane role on the docint account, so it cannot call the Read model.

## Design

### 1. Infra (`infra/modules/shared.bicep`)

- **Env var:** add `DocumentIntelligence__Endpoint` = `documentIntelligence.properties.endpoint`
  to `ragIndexerApp`'s container env, alongside the existing `AiSearch__Endpoint` and
  `AiFoundry__ProjectEndpoint`. Gate: `deployPhase2 && deployAiSearch` (matches the
  worker resource).
- **Role assignment:** add `ragIndexerDocIntUser` — scope `documentIntelligence`, role
  **Cognitive Services User** (`a97b65f3-24c7-4388-baec-2e87135dc908`, verified),
  `principalId` = `ragIndexerApp` system-assigned identity, `principalType`
  `ServicePrincipal`. Gate: `deployPhase2 && deployAiSearch`. Mirrors the existing
  `ragIndexer*` role-assignment pattern (Cosmos data, storage-blob reader, etc.).
- **Deploy** via the deployment stack (`az stack group create` — Deployment Stacks only,
  invariant #16) using the committed `main-shared.dev.local.bicepparam`.

### 2. Operational backfill (index the 6)

- Grant the operator identity (jim) **Cognitive Services User** on the docint account for
  the local run (Owner does not include data-plane Cognitive Services access).
- Set `DocumentIntelligence__Endpoint` locally and re-run `--run-rag-backfill`. With the
  endpoint present, the CLI registers `FallbackDocumentTextExtractor`; PdfPig returns
  `OcrRequired` for the image PDFs; ADI OCR extracts text; the chunker + indexer proceed
  normally. `rag_index_state` is empty, so no state-skip — the 6 are reprocessed.

## Cost

ADI Read model, S0 pricing ≈ **$1.50 / 1,000 pages**, billed only on documents that hit
the `OcrRequired` fallback (image PDFs). The 6 manuals total a few hundred pages →
~**$0.30 one-time**. Steady-state is near-zero (scanned docs are rare in this corpus).
Well within the $300–400/mo cap.

## Testing / verification

- **No new application code**, so no new unit tests. Existing tests already cover
  `FallbackDocumentTextExtractor` (PdfPig-success passthrough; `OcrRequired` → fallback).
- **Bicep**: `az bicep build` / stack `--what-if` clean; the new env + role render only
  under `deployPhase2 && deployAiSearch`.
- **Behavioural verification (the real proof):** after backfill, re-run the linked-vs-index
  cross-check — accepted-type linked docs missing from the AI Search index should drop
  from **6 → 0** (or ≤1 if ADI genuinely cannot read one scan). Confirm the ADI code path
  actually ran via the `AzureDocumentIntelligenceExtractor` log line and non-zero
  extracted-char count, and that new `doc_<id>` chunks for the 6 appear in the index.

## Risks & mitigations

- **Role GUID / scope errors** → verified the GUID live; scope to the docint account only
  (least privilege).
- **A scan ADI still can't read** → that document remains unindexed and is genuinely
  unrecoverable; acceptable, and logged visibly (no masking, invariant #17).
- **Deploy blast radius** → the change is additive (one env var + one role assignment),
  gated to Phase 2; stack `--what-if` reviewed before apply.

## Out of scope

- Any change to extraction / chunking / indexing logic (already built and tested).
- Broadening `AcceptedDocumentTypes` or OCR for non-PDF formats.
- The separate `DocumentDownloadService` blob-exists-skip-without-stamp file-info bug
  (found during investigation; tracked, orthogonal to OCR).
