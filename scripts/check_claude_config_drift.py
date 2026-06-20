"""Report whether vendored .claude/ files have drifted behind their upstream source.

Reads each vendored file's provenance header (vendored-from: <path> @ <sha>) and,
if the upstream APS.JimClaudeCodeConfig repo is available, checks whether the
source file has advanced past the recorded SHA. Degrades visibly (status
'source-missing') when the upstream repo is not present — never silently passes.

Files without a vendored-from header (README, INVARIANTS, authored rules, etc.)
are silently skipped — they are intentionally not vendored and produce no output.
"""
from __future__ import annotations
import re, subprocess, sys
from dataclasses import dataclass
from pathlib import Path

# Matches the vendored-from provenance comment anywhere in the file.
# Works whether the comment appears above the title OR below YAML frontmatter.
HEADER_RE = re.compile(
    r"vendored-from:\s*APS\.JimClaudeCodeConfig/(?P<path>\S+)\s*@\s*(?P<sha>[0-9a-f]{7,40})"
)
DEFAULT_SOURCE = Path(r"C:/aps/projects/APS.JimClaudeCodeConfig")

# Only scan these subdirectories of the vendored root — excludes .worktrees/, .superpowers/, etc.
VENDORED_SUBDIRS = ("rules", "skills", "commands", "agents")


def _iter_md(root: Path):
    """Yield .md files only from the four canonical vendored config subdirs."""
    for sub in VENDORED_SUBDIRS:
        d = root / sub
        if d.exists():
            yield from sorted(d.rglob("*.md"))


@dataclass
class Header:
    source_path: str    # e.g. global/rules/no-guessing.md
    recorded_sha: str


@dataclass
class Drift:
    path: str
    recorded_sha: str
    status: str     # current | behind | source-missing


def parse_header(text: str) -> Header | None:
    """Return the provenance header if the file carries a vendored-from comment, else None.

    The regex searches the full text so it finds the comment regardless of whether it
    appears above the markdown title (rules/commands/skills) or below YAML frontmatter
    (agent files). Files using 'authored-for:' or plain 'Derived from' — without the
    literal 'vendored-from: APS.JimClaudeCodeConfig/' token — correctly return None.
    """
    m = HEADER_RE.search(text)
    if not m:
        return None
    return Header(m.group("path"), m.group("sha"))


def _git(source: Path, *args: str) -> str | None:
    """Run a git command in the source repo; return stdout or None on failure."""
    try:
        out = subprocess.run(
            ["git", "-C", str(source), *args],
            capture_output=True, text=True, check=True,
        )
        return out.stdout.strip()
    except Exception:
        return None


def check_drift(vendored_root: Path, source: Path = DEFAULT_SOURCE) -> list[Drift]:
    """Walk vendored_root for .md files and check each vendored file against upstream.

    Files without a vendored-from header are silently skipped (not counted, not listed).
    A trailing summary count of skipped files is emitted by main() for transparency.
    """
    reports: list[Drift] = []
    source_ok = source.exists() and _git(source, "rev-parse", "HEAD") is not None

    for f in _iter_md(vendored_root):
        text = f.read_text(encoding="utf-8", errors="replace")
        h = parse_header(text)
        if h is None:
            # Legitimately non-vendored: skip silently
            continue

        rel = str(f.relative_to(vendored_root))

        if not source_ok:
            reports.append(Drift(rel, h.recorded_sha, "source-missing"))
            continue

        # Latest SHA that touched the source file on origin/main
        latest = _git(source, "log", "-1", "--format=%h", "origin/main", "--", h.source_path)
        if not latest:
            reports.append(Drift(rel, h.recorded_sha, "source-missing"))
            continue

        # Any commits to the source file after the recorded SHA?
        contains = _git(
            source, "log", "--format=%h", f"{h.recorded_sha}..origin/main", "--", h.source_path
        )
        status = "behind" if contains else "current"
        reports.append(Drift(rel, h.recorded_sha, status))

    return reports


def main() -> int:
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(".claude")
    reports = check_drift(root)

    behind = [r for r in reports if r.status == "behind"]
    missing = [r for r in reports if r.status == "source-missing"]

    # Count skipped (non-vendored) files for transparency — scoped to the same dirs
    all_scoped_md = list(_iter_md(root))
    skipped = len(all_scoped_md) - len(reports)

    for r in reports:
        print(f"{r.status:14} {r.path} (@{r.recorded_sha or '-'})")

    if skipped:
        print(f"\n({skipped} non-vendored .md skipped)")
    if missing:
        print(f"\nNOTE: {len(missing)} file(s) unverifiable (upstream absent or source path not found).")
    print(f"\n{len(behind)} file(s) behind upstream.")
    return 0  # informational; never fails the build


if __name__ == "__main__":
    raise SystemExit(main())
