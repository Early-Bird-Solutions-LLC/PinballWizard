"""Tests for check_claude_frontmatter.py — TDD: write first, run RED, then implement.

Purpose: catch the bug class where a provenance comment (or any content)
before YAML frontmatter pushes it off byte-0, breaking skill/command/agent
`description` parsing.
"""
from __future__ import annotations

import textwrap
from pathlib import Path

import pytest

from check_claude_frontmatter import check, Problem


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _make_claude_root(tmp_path: Path) -> tuple[Path, Path, Path, Path]:
    """Create skeleton .claude/ directory tree and return (root, skills/, agents/, commands/)."""
    root = tmp_path / ".claude"
    skills_dir = root / "skills"
    agents_dir = root / "agents"
    commands_dir = root / "commands"
    skills_dir.mkdir(parents=True)
    agents_dir.mkdir(parents=True)
    commands_dir.mkdir(parents=True)
    return root, skills_dir, agents_dir, commands_dir


def _write_skill(skills_dir: Path, name: str, content: str) -> Path:
    skill_dir = skills_dir / name
    skill_dir.mkdir(parents=True, exist_ok=True)
    p = skill_dir / "SKILL.md"
    p.write_text(content, encoding="utf-8")
    return p


def _write_agent(agents_dir: Path, name: str, content: str) -> Path:
    p = agents_dir / f"{name}.md"
    p.write_text(content, encoding="utf-8")
    return p


def _write_command(commands_dir: Path, name: str, content: str) -> Path:
    p = commands_dir / f"{name}.md"
    p.write_text(content, encoding="utf-8")
    return p


# ---------------------------------------------------------------------------
# Test 1: skill with proper byte-0 frontmatter + description → no problem
# ---------------------------------------------------------------------------

def test_skill_proper_frontmatter_no_problem(tmp_path):
    root, skills_dir, agents_dir, commands_dir = _make_claude_root(tmp_path)
    _write_skill(skills_dir, "my-skill", textwrap.dedent("""\
        ---
        name: my-skill
        description: Does something useful
        ---
        # My Skill
        Body text here.
    """))
    problems = check(root)
    assert problems == [], f"Expected no problems, got: {problems}"


# ---------------------------------------------------------------------------
# Test 2: skill with an HTML comment before --- → Rule A problem
# ---------------------------------------------------------------------------

def test_skill_comment_before_frontmatter_rule_a(tmp_path):
    root, skills_dir, agents_dir, commands_dir = _make_claude_root(tmp_path)
    _write_skill(skills_dir, "bad-skill", textwrap.dedent("""\
        <!-- provenance: auto-generated -->
        ---
        name: bad-skill
        description: Broken by leading comment
        ---
        # Bad Skill
    """))
    problems = check(root)
    assert len(problems) == 1
    assert "byte 0" in problems[0].reason.lower() or "precedes" in problems[0].reason.lower()
    assert "bad-skill" in problems[0].path


# ---------------------------------------------------------------------------
# Test 3: agent missing `description` → Rule B problem
# ---------------------------------------------------------------------------

def test_agent_missing_description_rule_b(tmp_path):
    root, skills_dir, agents_dir, commands_dir = _make_claude_root(tmp_path)
    # Agent with name: but NO description:
    _write_agent(agents_dir, "no-desc-agent", textwrap.dedent("""\
        ---
        name: no-desc-agent
        tools: Read, Grep
        ---
        # No Description Agent
    """))
    problems = check(root)
    assert len(problems) == 1
    assert "description" in problems[0].reason.lower()
    assert "no-desc-agent" in problems[0].path


# ---------------------------------------------------------------------------
# Test 4: command that is pure prose (no frontmatter) → no problem
# ---------------------------------------------------------------------------

def test_command_pure_prose_no_problem(tmp_path):
    root, skills_dir, agents_dir, commands_dir = _make_claude_root(tmp_path)
    _write_command(commands_dir, "my-command", textwrap.dedent("""\
        # Ship Command

        **Purpose:** Execute the complete commit → push → PR workflow.

        **Usage:** `/ship [options]`
    """))
    problems = check(root)
    assert problems == [], f"Expected no problems, got: {problems}"


# ---------------------------------------------------------------------------
# Test 5: command whose frontmatter sits below a leading comment → Rule A problem
# ---------------------------------------------------------------------------

def test_command_frontmatter_below_comment_rule_a(tmp_path):
    root, skills_dir, agents_dir, commands_dir = _make_claude_root(tmp_path)
    _write_command(commands_dir, "shifted-command", textwrap.dedent("""\
        <!-- auto-generated — do not edit -->
        ---
        name: shifted-command
        description: This frontmatter is shifted off byte 0
        ---
        Body content.
    """))
    problems = check(root)
    assert len(problems) == 1
    assert "byte 0" in problems[0].reason.lower() or "precedes" in problems[0].reason.lower()
    assert "shifted-command" in problems[0].path


# ---------------------------------------------------------------------------
# Test 6: smoke run against the REAL .claude directory → OK, zero problems
# ---------------------------------------------------------------------------

def test_real_claude_directory_is_clean():
    """The actual .claude/ in this repo must pass with zero problems."""
    import os
    # Walk up from scripts/ to find repo root, then into .claude/
    scripts_dir = Path(__file__).resolve().parent
    repo_root = scripts_dir.parent
    claude_dir = repo_root / ".claude"
    assert claude_dir.exists(), f".claude not found at {claude_dir}"
    problems = check(claude_dir)
    assert problems == [], (
        f"Real .claude has {len(problems)} problem(s):\n"
        + "\n".join(f"  {p.path}: {p.reason}" for p in problems)
    )


# ---------------------------------------------------------------------------
# Additional edge-case tests
# ---------------------------------------------------------------------------

def test_skill_missing_entirely_rule_b(tmp_path):
    """Skill with no frontmatter at all → Rule B (no byte-0 frontmatter block)."""
    root, skills_dir, agents_dir, commands_dir = _make_claude_root(tmp_path)
    _write_skill(skills_dir, "prose-skill", textwrap.dedent("""\
        # Prose Skill
        This skill has no frontmatter whatsoever.
    """))
    problems = check(root)
    assert len(problems) == 1
    assert "prose-skill" in problems[0].path


def test_agent_missing_name_rule_b(tmp_path):
    """Agent with description but no name → Rule B (name required for agents)."""
    root, skills_dir, agents_dir, commands_dir = _make_claude_root(tmp_path)
    _write_agent(agents_dir, "nameless-agent", textwrap.dedent("""\
        ---
        description: Does something but has no name key
        ---
        # Nameless
    """))
    problems = check(root)
    assert len(problems) == 1
    assert "name" in problems[0].reason.lower()
    assert "nameless-agent" in problems[0].path


def test_skill_rule_a_takes_priority_over_rule_b(tmp_path):
    """When frontmatter has intent but is shifted, Rule A fires (not Rule B)."""
    root, skills_dir, agents_dir, commands_dir = _make_claude_root(tmp_path)
    _write_skill(skills_dir, "both-broken", textwrap.dedent("""\
        # Some intro text
        ---
        name: both-broken
        description: shifted and not at byte 0
        ---
    """))
    problems = check(root)
    # Rule A should fire (frontmatter intent exists but not at byte 0)
    assert len(problems) >= 1
    rule_a = [p for p in problems if "byte 0" in p.reason.lower() or "precedes" in p.reason.lower()]
    assert len(rule_a) >= 1


def test_skill_nested_in_subdirectory(tmp_path):
    """Skills can be nested in subdirectories under skills/."""
    root, skills_dir, agents_dir, commands_dir = _make_claude_root(tmp_path)
    subdir = skills_dir / "subgroup"
    subdir.mkdir()
    skill_dir = subdir / "nested-skill"
    skill_dir.mkdir()
    p = skill_dir / "SKILL.md"
    p.write_text(textwrap.dedent("""\
        ---
        name: nested-skill
        description: A nested skill
        ---
        Body.
    """), encoding="utf-8")
    problems = check(root)
    assert problems == []


def test_worktrees_excluded(tmp_path):
    """Files inside <root>/worktrees/ must not be scanned."""
    root, skills_dir, agents_dir, commands_dir = _make_claude_root(tmp_path)
    # Put a broken SKILL.md under worktrees/
    wt_skill = root / "worktrees" / "my-branch" / "skills" / "wt-skill"
    wt_skill.mkdir(parents=True)
    (wt_skill / "SKILL.md").write_text("<!-- broken -->\n---\ndescription: x\n---\n", encoding="utf-8")
    problems = check(root)
    assert problems == [], f"worktrees/ content must be ignored, got: {problems}"
