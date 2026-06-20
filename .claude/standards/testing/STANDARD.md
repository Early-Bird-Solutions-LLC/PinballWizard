---
name: testing
id-prefix: TEST
status: active
applies-to:
  - "tests/**"
  - "src/PinballWizard.Core/ISourceScraper.cs"
  - "src/PinballWizard.Infrastructure/Scraping/**"
---

# Testing Standard

Tests assert behavior, not structure. A test named "deduplicates" must
include a fixture where dedup actually fires. Coverage is necessary but not
sufficient — tests are documentation of intent.

**RULE TEST-01** (behavior-not-structure)
WHEN:   adding or changing a test
THEN:   the test exercises behavior — a test named for an effect includes a fixture where that effect fires
NEVER:  write a test that merely restates the code's structure or asserts a constant
CHECK:  (qualitative — /local-review) — verify the named behavior is actually triggered by the fixture
SEV:    🔴
REF:    quality-spec · local-review cat 2

**RULE TEST-02** (source-alias-contract)
WHEN:   adding a new ISourceScraper
THEN:   SourceAliasContractTests pins the scraper Name to its --source alias and passes
NEVER:  add a scraper without the alias contract test green
CHECK:  dotnet test --filter "FullyQualifiedName~SourceAliasContractTests" --nologo
SEV:    🔴
REF:    CLAUDE.md (CLI) · PR-AUDIT#4

**RULE TEST-03** (sibling-no-drift)
WHEN:   a test is copied from a sibling scraper/repository test
THEN:   diff against the sibling for TryExtract wrappers, error boundaries, yield/break semantics, ctor null-checks, unused fields
NEVER:  copy a sibling test and leave drifted error-handling or assertions
CHECK:  (qualitative — /local-review) — sibling diff
SEV:    ⚠️
REF:    PR-AUDIT#2 · local-review cat 4

**RULE TEST-04** (naming-convention)
WHEN:   naming a test method
THEN:   follow Method_State_Expectation
NEVER:  use an opaque test name that hides what is asserted
CHECK:  (qualitative — /local-review) — test-name convention
SEV:    ⚠️
REF:    quality-spec

## Definition of Done

- TEST-01: named behavior is actually triggered.
- TEST-02: SourceAliasContractTests green for new scrapers.
- TEST-03: sibling-copied tests diffed for drift.
- TEST-04: Method_State_Expectation naming.
