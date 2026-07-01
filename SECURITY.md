# Security Policy

This document describes the supported version scope, vulnerability reporting process, and security posture for PinballWizard.

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
project maintainer at [jim@earlybirdsolutions.com](mailto:jim@earlybirdsolutions.com).

## What's in scope

- The scraper (`PinballWizard.Cli` and the libraries it composes) — any
  vulnerability that could let a malicious source site or attacker
  compromise the host running the scraper, exfiltrate data, or persist
  state across runs.
- The HTTP API (`PinballWizard.Api`) and Blazor web front end (`PinballWizard.Web`)
  — authentication bypass, privilege escalation, injection, or data
  exfiltration paths.
- The Docker image as published — privilege escalation, supply-chain
  issues, embedded secrets.
- The CI workflows in `.github/workflows/` — workflow injection,
  unsanitized inputs, leaked credentials.
- The dependency graph — vulnerable transitive packages with realistic
  exploit paths under the project's usage.

## What's out of scope

- The third-party sites this scraper crawls (10+ manufacturer sites) —
  vulnerabilities on those sites belong to their operators.
- Self-inflicted issues from running with credentials this project does
  not require (e.g., setting `AZURE_*` secrets the scraper has no use
  for).

## Disclosure

We follow [coordinated disclosure](https://en.wikipedia.org/wiki/Coordinated_vulnerability_disclosure):

- We will work with you on a fix and a public disclosure timeline.
- Default disclosure window is **90 days** from acknowledgment, sooner if
  a fix is straightforward, longer by mutual agreement if the issue is
  complex.
- We will credit reporters in the advisory unless they prefer otherwise.

## Hardening posture

The scraper is designed to **read from public sources and write to local
storage**. The deployed platform (API + Blazor web + RAG ingestion worker
on Azure Container Apps) follows a zero-secret architecture: Managed
Identity + RBAC, no API keys, no shared keys on Storage. The custom
[`sanitization.yml`](.github/workflows/sanitization.yml) workflow blocks
common credential patterns from being committed.

CodeQL runs on every PR and weekly. Dependency updates are automated
([ADR-0037](docs/adr/0037-dependency-update-automation.md)): Renovate
proposes grouped version updates (majors held for review), and Dependabot
opens immediate security PRs for any published advisory — which matters
because vulnerable advisories are build-breaking (`NU1903`) here.
Locked-mode NuGet restore prevents version drift.
