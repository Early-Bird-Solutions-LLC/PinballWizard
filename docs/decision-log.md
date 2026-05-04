# Decision log

Sub-ADR decisions for PinballWizard. Append-only. ADRs (in [`adr/`](adr/)) capture architectural decisions with significant trade-offs and alternatives. This log captures the smaller decisions: tool versions within a category, library choices, parameter values, naming conventions, threshold settings — anything worth retrieving later but too small to justify a full ADR.

Per [`guardrails.md`](guardrails.md) § "Decision log": format per entry is

```text
## YYYY-MM-DD — [Short title]
**Decision:** ...
**Alternatives considered:** ...
**Rationale:** ...
**Revisit when:** ...
**Related:** PR #XX, ADR-YYYY (if any)
```

Decisions reverse via a new entry that supersedes the prior one (with a back-reference); never edit history.

## When to add an entry vs. write an ADR

If **all four** of these are true, write an ADR (`adr/00NN-...md`) instead of a decision-log entry:

1. The decision has significant trade-offs.
2. Alternatives were genuinely considered (not default-accepted).
3. Consequences extend beyond the immediate PR.
4. Future readers (including future-Claude) would benefit from the permanent, formally-structured record.

Otherwise, this log is the right home.

---

<!-- New entries append below this marker, newest at the top. -->

## 2026-05-04 — Sanitization rules (Item 9) verified locally, not via synthetic-commit CI run

**Decision:** Phase 2 § Scope Item 9 hand-off ("synthetic-token verification") for the sanitization workflow's three email-rule branches is closed via local `grep -E -i` verification rather than via two synthetic test commits pushed to throwaway branches as originally specified in `build-spec.md`.

**Alternatives considered:**

- Push two synthetic test commits to throwaway branches, observe CI fail, delete branches (the originally-specified protocol). Rejected: even with `gh pr close --delete-branch`, the commits remain accessible by SHA via the GitHub API (referenced by the closed PR's commit history) for the ~90-day reflog garbage-collection window. The strings that *trigger* the patterns must by definition match the patterns the rules exist to block — pushing them anywhere on the remote, even briefly, defeats the rule's purpose at a small but real reputational cost. The first attempt at this protocol pushed the user's literal work email to PR #77 before the leak was caught and PR #77 was closed without merging.
- Skip verification entirely and trust the rule's wiring. Rejected: without active verification, the workflow's `if [ -n "${WORK_EMAIL_PATTERN:-}" ]` gate could silently no-op (e.g., the secret value is whitespace, malformed, or the named-secret lookup fails) and a future leak would land on `main` without anyone noticing.
- Mock the workflow's `run_rule` invocation in a unit test under `tests/`. Rejected: the workflow is bash, the project's test suite is xUnit + .NET — no natural place to put a bash test, and a CI YAML test that runs in a separate workflow against the sanitization YAML adds infrastructure for a one-time verification.

**Rationale:** Local `grep -E -i <pattern>` against synthetic placeholder strings (`jim@earlybird-placeholder.invalid`, `noreply@earlybirdsolutions.invalid`, `pattern-test@distilledtech.com`) piped via stdin (no disk writes, no commits) exercises the *exact same* matcher the workflow uses (`grep -E -i "$WORK_EMAIL_PATTERN"` at sanitization.yml:115). Both positive (string matches → rule fires) and negative (similar-but-non-matching strings) cases are confirmed:

| Rule | Pattern | Positive case | Negative case |
| ---- | ------- | ------------- | ------------- |
| Personal email | `jim@earlybird` | `jim@earlybird-placeholder.invalid` → match ✅ | `unrelated-text@otherdomain.example` → no match ✅ |
| Personal domain | `@earlybirdsolutions` | `noreply@earlybirdsolutions.invalid` → match ✅ | `noreply@earlybird.io` → no match ✅ |
| Work email | `@distilledtech\.com` | `pattern-test@distilledtech.com` → match ✅ | `someone@distilledtechXcom` → no match (escape works) ✅ |

The pattern's ERE validity check (sanitization.yml:109 — `printf '' \| grep -E "$WORK_EMAIL_PATTERN"`) returns `rc=1` (no match against empty input), not `rc=2` (malformed pattern), confirming the secret value is a well-formed ERE.

**Revisit when:** A change to the sanitization workflow's matcher logic (e.g., switching from `grep -E` to `ripgrep` or to a different regex flavor) — that would require re-validating the same patterns under the new matcher. Or if a PR ever lands on `main` with one of these patterns inside, indicating the workflow regressed silently.

**Related:** `.github/workflows/sanitization.yml` lines 87–119 (the rule definitions), `feedback_personal_identity_only.md` (the policy these rules enforce), `build-spec.md` Phase 2 § Hand-off outcomes (Item 9 status). PR #77 (the abandoned synthetic-commit attempt — closed without merge after the leak was caught; commits remain in GitHub reflog for ~90 days).

## 2026-05-04 — Stern Playwright DTOs stay as classes, not records

**Decision:** `LinkRaw` (in `GamePageScraper`) and `BulletinRaw` (in `ServiceBulletinScraper`) — the DTO types Playwright deserializes `page.EvaluateAsync<T>()` results into — are `internal sealed class` with `[JsonPropertyName] public T Foo { get; set; }` properties. They are explicitly **not** positional records.

**Alternatives considered:**

- Positional records with `[property: JsonPropertyName(...)]` (PR #72's approach). Rejected: Playwright's `EvaluateArgumentValueConverter.ToExpectedType` calls `Activator.CreateInstance(t)` and walks properties — positional records have no parameterless ctor, so this throws `MissingMethodException` at runtime.
- Non-positional records with `init` setters. Rejected: Playwright's converter assigns properties via the setter at runtime, after the object already exists; `init` setters reject post-construction assignment.
- Custom `JsonConverter<T>` to force STJ deserialization. Rejected: Playwright's converter is hardcoded inside `EvaluateArgumentValueConverter` and does not consult STJ converters for typed deserialization.

**Rationale:** PR #72 reverted these to positional records on the assumption that Playwright 1.59 had switched to System.Text.Json. The post-merge live-site validation (Phase 2 § Scope item 6 hand-off, run 2026-05-04) surfaced the regression: `MissingMethodException: Cannot dynamically create an instance of type '…+BulletinRaw'. Reason: No parameterless constructor defined.` Stack trace pinpointed `EvaluateArgumentValueConverter.ToExpectedType`, confirming Playwright 1.59 still uses Activator-based deserialization (same as 1.12 from PR #34's original workaround). The PR #72 unit tests pinned STJ deserialization, which positional records satisfy — but Playwright never invokes STJ for typed `EvaluateAsync<T>` results. Tests pinned the wrong path.

**Revisit when:** A future Playwright release (post-1.59) genuinely switches to STJ-based deserialization for `EvaluateAsync<T>`. Indicator: source-link in the stack trace no longer references `EvaluateArgumentValueConverter`. Until then, this stays.

**Related:** PR #34 (original class workaround), PR #72 (failed records revert — superseded by this decision), PR currently open against this branch (this revert + Activator-based contract test). See also `tests/PinballWizard.Scraper.Tests/Scraping/Stern/SternPlaywrightDtoActivatorContractTests.cs` for the contract tests that now pin the actual Activator path.
