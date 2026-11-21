# PR first-pass review knowledge base

Store for **promoted** lessons for the CI Propose agent defined in
[ADR-0069](../../../docs/adr/0069-pr-first-pass-review-agent.md).

## Write rules (AGENT-05 / ADR-0066)

- Only authenticated human promotion (`promoted_by` = GitHub user id / login).
- Source must cite `source_run_id` (Actions run id or artifact) and `content_hash`.
- GitHub emoji reactions are **never** promotion authority.
- Entries are immutable versions; edits add a new file/version, never rewrite.

## Read rules (AGENT-13)

- Retrieval (when wired) must inject under an untrusted-context delimiter.
- Do not retrieve `raw_run_excerpt` into any future Act path.

## Layout

| Path | Purpose |
| --- | --- |
| `playbooks/` | Hand-written playbook snippets (`trust: playbook`) |
| `lessons/` | Human-promoted lessons (`trust: promoted_lesson`) |
| `schema/lesson.schema.json` | Required provenance fields |

v1 ships the schema only — no auto-ingest. Wiring retrieval into the workflow
prompt is deferred until the transitional kit item closes (expiry 2026-12-31).
