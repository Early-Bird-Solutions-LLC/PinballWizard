---
name: context-management
description: >-
  Analyze and clean up conversation context to free token space
---
<!-- vendored-from: APS.JimClaudeCodeConfig/global/skills/context-management/SKILL.md @ 6dfd2cf
     adapted-for: PinballWizard (verbatim)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

# Context Management Skill

**Version:** 2.0 | **Auto-Trigger:** Yes | **Scope:** All projects
**Context:** ~60 lines (reference) / ~150 lines (full)

---

## Quick Reference

### Triggers

- "context is getting full", "reduce context", "clean up context"
- "context management", "show context", "context usage"
- Context exceeds 60% (automatic proactive warning)

### Context Categories

| Category | Criteria | Action |
|----------|----------|--------|
| **Safe to Remove** | Files saved to disk, docs not edited in 10+ messages | Auto-removable (with confirmation) |
| **Suggest Reviewing** | Completed plans, large skill files, old exploration | User decides |
| **Must Keep** | Files edited recently, current plan, active work | Never remove |
| **Compactable** | Long conversation history (20+ messages) | Suggest `/compact` |

### Quick Actions

| Option | Effect |
|--------|--------|
| A) Remove all safe | Remove documentation files saved to disk |
| B) Remove only docs | Remove only *.md, *.txt files |
| C) Review each | Interactive selection |
| D) Run /compact | Compress conversation history |
| E) Cancel | No changes |

### Token Estimates

| File Size | Tokens |
|-----------|--------|
| Small (<100 lines) | ~2-5k |
| Medium (100-500 lines) | ~5-20k |
| Large (>500 lines) | ~20-50k |
| Conversation (per 10 messages) | ~2-5k |

### Safety Rules (NEVER Violate)

1. **Never auto-remove** - Always confirm with user
2. **Never remove active work** - Files edited in last 10 messages
3. **Never lose data** - Only suggest removing files saved to disk
4. **Always explain** - What's lost, how to get it back

---

<!-- DETAILED DOCUMENTATION BELOW -->

## Detailed Documentation

### Proactive Warning Thresholds

| Context % | Action |
|-----------|--------|
| 60% | Warn user, offer cleanup |
| 75% | Remind again if previously declined |
| After 3+ docs created | Suggest cleanup |
| After ExitPlanMode | Offer to archive plan |

### Analysis Procedure

**Step 1: Categorize Files**
- Green (Safe): Docs saved to disk, not recently edited
- Yellow (Review): Completed plans, large skills, old exploration
- Red (Keep): Recent edits, active work, CLAUDE.md
- Blue (Compact): Long conversation threads

**Step 2: Calculate Token Estimates**
```
tokens ≈ line_count × 50 × type_multiplier
- Markdown/text: ×0.8
- JSON/YAML: ×1.2
- Code: ×1.0
```

**Step 3: Present Interactive Breakdown**
```
Context Analysis (XX% used)
━━━━━━━━━━━━━━━━━━━━━━━━━━━
✅ SAFE TO REMOVE (N files, ~XXk tokens)
⚠️ SUGGEST REVIEWING (N items)
🔴 MUST KEEP (N files)

Quick Actions: A/B/C/D/E
```

### Educational Tips

- "Removing files from context is like closing a browser tab - the page still exists"
- "Can re-read anytime with: Read(file_path: '...')"
- "Context above 70% can impact response quality"

### File Type Detection

**Documentation:** `*.md`, `*.txt`, `README*`, `*-GUIDE.md`
**Scripts:** `*.ps1`, `*.sh` (if not active project code)
**Plans:** Files in `.claude/plans/`
**Active:** Files with Edit/Write in last 10 messages

---

**Version:** 2.0 (Reference Card pattern - reduced from 420 to ~100 lines)
