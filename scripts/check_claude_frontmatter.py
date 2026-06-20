"""Validate YAML frontmatter placement in .claude/ skill/agent/command files.

Catches the bug class where a provenance comment (or any text) before the
YAML frontmatter delimiter pushes it off byte-0, breaking Claude Code's
skill/command/agent `description` and `name` parsing.

Rules
-----
Rule A (byte-0):
    If a file has "frontmatter intent" (a ``---`` line AND a ``name:`` or
    ``description:`` key within the first 15 lines), then its very first line
    MUST be exactly ``---``.

Rule B (required + complete) — skills and agents only:
    A skill or agent MUST have a byte-0 frontmatter block.  That means:
    - Line 1 is ``---`` (opening delimiter)
    - A closing ``---`` appears later in the file
    - A ``description:`` key is inside that block
    - For agents: a ``name:`` key is also inside that block

Commands:
    Only Rule A applies.  Frontmatter is optional; if no frontmatter intent
    is detected the file is fine.

Usage
-----
    python check_claude_frontmatter.py [<root>]

    Defaults <root> to ``.claude`` in the current directory.

    Exit 0 — no problems.
    Exit 1 — one or more problems; each printed as ``PROBLEM <path>: <reason>``.
"""
from __future__ import annotations

import sys
from dataclasses import dataclass
from pathlib import Path

_FRONT_WINDOW = 15  # lines to inspect for "frontmatter intent"


@dataclass(frozen=True)
class Problem:
    path: str
    reason: str

    def __repr__(self) -> str:  # pragma: no cover
        return f"Problem(path={self.path!r}, reason={self.reason!r})"


# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------

def _read_lines(path: Path, limit: int = 200) -> list[str]:
    """Return up to *limit* stripped lines from *path* (UTF-8, fallback latin-1)."""
    try:
        text = path.read_text(encoding="utf-8")
    except UnicodeDecodeError:
        text = path.read_text(encoding="latin-1")
    return text.splitlines()[:limit]


def _has_frontmatter_intent(lines: list[str]) -> bool:
    """True when the first _FRONT_WINDOW lines contain ``---`` AND a key line."""
    window = lines[:_FRONT_WINDOW]
    has_delim = any(ln.strip() == "---" for ln in window)
    has_key = any(
        ln.strip().startswith("name:") or ln.strip().startswith("description:")
        for ln in window
    )
    return has_delim and has_key


def _frontmatter_block(lines: list[str]) -> list[str] | None:
    """Return the lines inside the opening byte-0 frontmatter block, or None.

    Requires line 0 == '---'.  Searches for the closing '---' and returns
    everything in between (exclusive).  Returns None if not found.
    """
    if not lines or lines[0].strip() != "---":
        return None
    for i, ln in enumerate(lines[1:], start=1):
        if ln.strip() == "---":
            return lines[1:i]
    return None


def _check_file(path: Path, kind: str) -> list[Problem]:
    """Return problems for a single file.

    *kind* is one of ``"skill"``, ``"agent"``, or ``"command"``.
    """
    lines = _read_lines(path)
    problems: list[Problem] = []
    rel = path.as_posix()

    has_intent = _has_frontmatter_intent(lines)

    # --- Rule A -------------------------------------------------------
    first_line_is_delim = bool(lines) and lines[0].strip() == "---"

    if has_intent and not first_line_is_delim:
        problems.append(Problem(
            path=rel,
            reason=(
                "frontmatter not at byte 0 "
                "(something precedes the opening ---)"
            ),
        ))
        # Rule A fires → for commands, we're done.  For skills/agents, Rule B
        # would also fire (no byte-0 block) but reporting two problems for the
        # same file is noisy; Rule A is the actionable one.
        return problems

    # --- Rule B (skills and agents only) --------------------------------
    if kind in ("skill", "agent"):
        block = _frontmatter_block(lines)

        if block is None:
            # Either no opening --- on line 0, or no closing ---
            if first_line_is_delim:
                problems.append(Problem(
                    path=rel,
                    reason="frontmatter block opened but never closed (missing closing ---)",
                ))
            else:
                problems.append(Problem(
                    path=rel,
                    reason="missing required byte-0 frontmatter block",
                ))
            return problems

        block_text = "\n".join(block)
        has_description = any(
            ln.strip().startswith("description:") for ln in block
        )
        has_name = any(
            ln.strip().startswith("name:") for ln in block
        )

        if not has_description:
            problems.append(Problem(
                path=rel,
                reason="frontmatter block is missing required description: key",
            ))

        if kind == "agent" and not has_name:
            problems.append(Problem(
                path=rel,
                reason="frontmatter block is missing required name: key",
            ))

    return problems


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def check(root: Path) -> list[Problem]:
    """Scan *root* and return all frontmatter problems found.

    Scans:
    - ``<root>/skills/**/SKILL.md``  (recursive, excludes worktrees)
    - ``<root>/agents/*.md``
    - ``<root>/commands/*.md``

    Does NOT recurse into ``<root>/worktrees``.
    """
    problems: list[Problem] = []
    worktrees_prefix = (root / "worktrees").as_posix()

    # Skills — recursive glob for SKILL.md anywhere under skills/
    skills_root = root / "skills"
    if skills_root.exists():
        for skill_md in skills_root.rglob("SKILL.md"):
            # Skip anything under worktrees
            if skill_md.as_posix().startswith(worktrees_prefix):
                continue
            problems.extend(_check_file(skill_md, "skill"))

    # Agents — *.md directly under agents/
    agents_root = root / "agents"
    if agents_root.exists():
        for agent_md in sorted(agents_root.glob("*.md")):
            if agent_md.as_posix().startswith(worktrees_prefix):
                continue
            problems.extend(_check_file(agent_md, "agent"))

    # Commands — *.md directly under commands/
    commands_root = root / "commands"
    if commands_root.exists():
        for cmd_md in sorted(commands_root.glob("*.md")):
            if cmd_md.as_posix().startswith(worktrees_prefix):
                continue
            problems.extend(_check_file(cmd_md, "command"))

    return problems


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

def main() -> int:
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(".claude")
    problems = check(root)

    if not problems:
        # Count files checked
        worktrees_prefix = (root / "worktrees").as_posix()
        n = 0
        for skills_md in (root / "skills").rglob("SKILL.md") if (root / "skills").exists() else []:
            if not skills_md.as_posix().startswith(worktrees_prefix):
                n += 1
        for agents_md in (root / "agents").glob("*.md") if (root / "agents").exists() else []:
            n += 1
        for cmd_md in (root / "commands").glob("*.md") if (root / "commands").exists() else []:
            n += 1
        print(f"OK — {n} files checked, no frontmatter problems")
        return 0

    for p in problems:
        print(f"PROBLEM {p.path}: {p.reason}")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
