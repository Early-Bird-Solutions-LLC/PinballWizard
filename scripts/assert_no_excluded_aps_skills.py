"""Fail if any excluded APS skill leaked into .claude/skills/."""
from __future__ import annotations
import sys
from pathlib import Path

EXCLUDED = {
    "jira", "work-item-time-tracking", "azure-devops-pipeline", "teamcity",
    "basecamp", "linear", "sonarqube", "ado-wiki-edit", "investigate",
    "vpn-troubleshoot", "sso-troubleshoot", "ssl-certificate",
    "azure-sql-optimizer", "aps-devops-agent-pool", "setup-azure", "spec-driven",
}

def find_leaked(skills_dir: Path) -> list[str]:
    if not skills_dir.exists():
        return []
    present = {p.name for p in skills_dir.iterdir() if p.is_dir()}
    # any aps-*-standard dir is also a leak
    leaks = sorted((present & EXCLUDED) | {n for n in present if n.startswith("aps-")})
    return leaks

def main() -> int:
    leaked = find_leaked(Path(sys.argv[1]) if len(sys.argv) > 1 else Path(".claude/skills"))
    if leaked:
        print("LEAKED excluded APS skills:", ", ".join(leaked))
        return 1
    print("OK — no excluded APS skills present.")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
