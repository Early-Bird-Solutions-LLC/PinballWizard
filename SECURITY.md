# Security Policy

## Supported Versions

PinballWizard is pre-1.0 and ships from the `main` branch. Security fixes
land on `main` and are picked up by the next published release. There are
no parallel maintenance branches.

## Reporting a Vulnerability

**Please do not open public GitHub issues for security vulnerabilities.**

Use [GitHub's private security advisory flow](https://github.com/Early-Bird-Solutions-LLC/PinballWizard/security/advisories/new):

1. Open a draft advisory with a description of the issue.
2. Include reproduction steps, the affected code paths, and (if known)
   the conditions under which the issue is exploitable.
3. Suggest a remediation if you have one in mind. The maintainer will
   respond within **72 hours** with an acknowledgment and an initial
   assessment.

If GitHub Security Advisories are not available to you, email the
project maintainer at the address in the GitHub profile linked from this
repo's owner.

## What's in scope

- The scraper (`PinballWizard.Cli` and the libraries it composes) — any
  vulnerability that could let a malicious source site or attacker
  compromise the host running the scraper, exfiltrate data, or persist
  state across runs.
- The Docker image as published — privilege escalation, supply-chain
  issues, embedded secrets.
- The CI workflows in `.github/workflows/` — workflow injection,
  unsanitized inputs, leaked credentials.
- The dependency graph — vulnerable transitive packages with realistic
  exploit paths under the project's usage.

## What's out of scope

- The third-party site this scraper crawls (`sternpinball.com`) — those
  belong to its operators.
- Self-inflicted issues from running with credentials this project does
  not require (e.g., setting `AZURE_*` secrets the scraper has no use
  for).
- Findings against the planned-but-unbuilt Phase 2 platform until that
  code lands in this repository.

## Disclosure

We follow [coordinated disclosure](https://en.wikipedia.org/wiki/Coordinated_vulnerability_disclosure):

- We will work with you on a fix and a public disclosure timeline.
- Default disclosure window is **90 days** from acknowledgment, sooner if
  a fix is straightforward, longer by mutual agreement if the issue is
  complex.
- We will credit reporters in the advisory unless they prefer otherwise.

## Hardening posture

The scraper is designed to **read from public sources and write to local
storage** — no secrets, no inbound API, no persistent service surface.
Phase 2's planned platform is documented in
[`docs/infra_analysis.md`](docs/infra_analysis.md) and follows a
zero-secret architecture (Managed Identity + RBAC, no API keys, no
shared keys on Storage). The custom
[`sanitization.yml`](.github/workflows/sanitization.yml) workflow blocks
common credential patterns from being committed.

CodeQL runs on every PR and weekly. Dependabot proposes weekly
dependency updates. Locked-mode NuGet restore prevents version drift.
