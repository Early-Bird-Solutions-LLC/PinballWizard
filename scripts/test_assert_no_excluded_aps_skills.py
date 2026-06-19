import tempfile, pathlib
from assert_no_excluded_aps_skills import find_leaked, EXCLUDED

def test_flags_excluded(tmp_path):
    (tmp_path / "jira").mkdir()
    (tmp_path / "commit").mkdir()
    leaked = find_leaked(tmp_path)
    assert "jira" in leaked
    assert "commit" not in leaked

def test_clean_tree(tmp_path):
    (tmp_path / "commit").mkdir()
    (tmp_path / "pr").mkdir()
    assert find_leaked(tmp_path) == []
