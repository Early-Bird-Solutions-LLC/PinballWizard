import subprocess, sys, pathlib, tempfile, textwrap
from check_claude_config_drift import parse_header, Drift


def test_parse_header_extracts_path_and_sha():
    text = textwrap.dedent('''\
        <!-- vendored-from: APS.JimClaudeCodeConfig/global/rules/no-guessing.md @ 6dfd2cf
             adapted-for: PinballWizard (verbatim — universal engineering rule)
             last-synced: 2026-06-19 -->
        # No Guessing Rule
        ''')
    h = parse_header(text)
    assert h.source_path == "global/rules/no-guessing.md"
    assert h.recorded_sha == "6dfd2cf"


def test_parse_header_none_when_absent():
    assert parse_header("# plain file, no header\n") is None


def test_parse_header_none_for_authored_header():
    # authored-for / Derived from is NOT a vendored-from header — must be skipped
    text = textwrap.dedent('''\
        <!-- authored-for: PinballWizard — replaces APS mandatory-workflows.md.
             Derived from APS.JimClaudeCodeConfig/global/rules/mandatory-workflows.md @ 6dfd2cf
             last-synced: 2026-06-19 -->
        # PinballWizard Workflows
        ''')
    assert parse_header(text) is None


def test_parse_header_finds_header_below_yaml_frontmatter():
    # Agent files: YAML frontmatter first, then the vendored-from comment
    text = textwrap.dedent('''\
        ---
        name: codebase-analyzer
        description: Analyzes codebase implementation details.
        tools: Read, Grep, Glob, LS
        model: sonnet
        ---
        <!-- vendored-from: APS.JimClaudeCodeConfig/global/agents/codebase-analyzer.md @ 6dfd2cf
             adapted-for: PinballWizard (verbatim) -->
        # Codebase Analyzer
        ''')
    h = parse_header(text)
    assert h is not None
    assert h.source_path == "global/agents/codebase-analyzer.md"
    assert h.recorded_sha == "6dfd2cf"


def test_headerless_file_not_reported_as_drift():
    """Files with no vendored-from header must be silently skipped — not listed in drift reports."""
    import tempfile, pathlib
    from check_claude_config_drift import check_drift

    with tempfile.TemporaryDirectory() as tmpdir:
        root = pathlib.Path(tmpdir)
        # One vendored file (will be source-missing since no upstream repo)
        vendored = root / "rules" / "no-guessing.md"
        vendored.parent.mkdir()
        vendored.write_text(
            "<!-- vendored-from: APS.JimClaudeCodeConfig/global/rules/no-guessing.md @ 6dfd2cf -->\n# No Guessing\n",
            encoding="utf-8",
        )
        # One headerless file — must NOT appear in results
        headerless = root / "README.md"
        headerless.write_text("# README — no vendored header\n", encoding="utf-8")

        reports = check_drift(root, source=pathlib.Path("/nonexistent"))

    paths = [r.path for r in reports]
    assert not any("README" in p for p in paths), (
        f"Headerless README.md must be skipped, but got: {paths}"
    )
    # The vendored file should appear (source-missing since upstream absent)
    assert any("no-guessing" in p for p in paths)
    assert all(r.status != "no-header" for r in reports), (
        "no-header status must never appear in drift reports"
    )


def test_worktrees_dir_excluded_from_scan():
    """Files inside .worktrees/ snapshots must never appear in drift reports."""
    import tempfile, pathlib
    from check_claude_config_drift import check_drift

    with tempfile.TemporaryDirectory() as tmpdir:
        root = pathlib.Path(tmpdir)

        # A legitimate vendored file under rules/
        vendored = root / "rules" / "x.md"
        vendored.parent.mkdir(parents=True)
        vendored.write_text(
            "<!-- vendored-from: APS.JimClaudeCodeConfig/global/rules/x.md @ 6dfd2cf -->\n# X\n",
            encoding="utf-8",
        )

        # A worktree snapshot that also has a vendored-from header — must be excluded
        worktree_copy = root / "worktrees" / "snap" / ".claude" / "rules" / "y.md"
        worktree_copy.parent.mkdir(parents=True)
        worktree_copy.write_text(
            "<!-- vendored-from: APS.JimClaudeCodeConfig/global/rules/y.md @ 6dfd2cf -->\n# Y\n",
            encoding="utf-8",
        )

        reports = check_drift(root, source=pathlib.Path("/nonexistent"))

    paths = [r.path for r in reports]
    assert len(reports) == 1, f"Expected exactly 1 report (rules/x.md), got: {paths}"
    assert any("x.md" in p for p in paths), f"rules/x.md must be in reports, got: {paths}"
    assert not any("y.md" in p for p in paths), f"worktrees/snap/y.md must NOT be in reports, got: {paths}"
