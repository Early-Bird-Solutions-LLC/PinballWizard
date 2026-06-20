<!-- vendored-from: APS.JimClaudeCodeConfig/global/commands/clean-context.md @ 6dfd2cf
     adapted-for: PinballWizard (adapted: example output path replaced with a generic placeholder)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

# /clean-context - Smart Context Cleanup Command

Analyze current session context and provide intelligent cleanup recommendations using the context-management skill.

## Usage

```bash
/clean-context          # Interactive mode - analyze and ask for confirmation
/clean-context auto     # Auto mode - remove only SAFE items (still confirms)
/clean-context report   # Read-only - show breakdown without making changes
```

## What It Does

1. **Analyzes** current context usage and categorizes content:
   - 🟢 **Safe to remove:** Documentation files saved to disk
   - 🟡 **Suggest reviewing:** Completed plans, old exploration
   - 🔴 **Must keep:** Active work files, recent edits
   - 💬 **Compactable:** Long conversation history

2. **Estimates** token usage for each category

3. **Presents** clear recommendations with quick actions

4. **Executes** cleanup based on your choice (with confirmation)

## Interactive Mode (Default)

**Command:** `/clean-context`

**Behavior:**
- Shows full analysis breakdown
- Lists all removable items individually
- Provides quick action menu (A/B/C/D/E)
- Asks for confirmation before removing anything
- Shows before/after token counts

**Best for:**
- First time using the command
- Want to review what will be removed
- Learning context hygiene

## Auto Mode

**Command:** `/clean-context auto`

**Behavior:**
- Automatically identifies SAFE items (docs saved to disk)
- Shows summary of what will be removed
- Asks single yes/no confirmation
- Executes removal if confirmed
- Skips items marked as "suggest" or "keep"

**Best for:**
- Quick cleanup after creating docs
- You trust the safety categorization
- Want fastest cleanup experience

## Report Mode

**Command:** `/clean-context report`

**Behavior:**
- Shows full context breakdown
- Displays recommendations
- **Does NOT remove anything**
- No prompts for action

**Best for:**
- Just checking context status
- Understanding what's using space
- Planning when to do cleanup

## Example Output

### Interactive Mode
```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
📊 Context Analysis (57% used - 114k/200k tokens)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

✅ SAFE TO REMOVE - Documentation Files (4 files, ~55k tokens)

1. 📄 QUICK-TEST-QA.md
   │  327 lines • Saved to: disk
   │  Last modified: 15 mins ago
   │  ✓ Can re-read from disk anytime with: Read tool
   └─ Remove? [Y/n]

[... more files ...]

⚠️  SUGGEST KEEPING - Still Relevant (2 items)

5. 📋 implementation-plan.md (Implementation plan)
   │  Reason: Work still in progress
   │  Action: Keep until done
   └─ Keep [Y/n]

[... more items ...]

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
⚡ Quick Actions
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

A) Remove all safe items               [-55k tokens → 59k total]
B) Remove only documentation           [-40k tokens → 74k total]
C) Review each individually            [Interactive selection]
D) Keep everything, run /compact       [Compress history]
E) Cancel                              [No changes]

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
💡 Context Hygiene Tip
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

All documentation files above are saved to disk. Removing them
from context is safe - you can instantly re-read them with the
Read tool.

This is like closing a browser tab - the webpage still exists!

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Your choice: _____
```

### Auto Mode
```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
🚀 Auto Cleanup Mode
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Found 4 documentation files safe to remove:
  📄 QUICK-TEST-QA.md (327 lines, ~18k tokens)
  📄 README-DATABASE-SEEDING.md (226 lines, ~12k tokens)
  📄 TESTING-GUIDE.md (371 lines, ~20k tokens)
  📄 pre-flight-check.ps1 (186 lines, ~5k tokens)

Impact:
  Before: 114k tokens (57%)
  After:   59k tokens (30%)
  Saved:   55k tokens

All files are saved to disk and can be re-read anytime.

Remove these 4 files from context? (yes/no): _____
```

### Report Mode
```
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
📊 Context Report (57% used - 114k/200k tokens)
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

Breakdown:
  📄 Documentation files:        55k tokens (4 files) - Removable
  📋 Completed plans:            15k tokens (1 file)  - Suggest remove
  ✅ Active work:                 8k tokens (3 files) - Keep
  📖 Reference skills:           25k tokens (1 file)  - Keep for now
  💬 Conversation history:       11k tokens           - Can compact

Optimization Opportunities:
  ✅ Remove 4 documentation files → Save 55k tokens
  ⚠️  Archive completed plan     → Save 15k tokens
  🔄 Run /compact                → Save ~6k tokens

Potential savings: 70-76k tokens (could reach 30-35% context usage)

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

To execute cleanup: /clean-context
For auto cleanup:   /clean-context auto
```

## When to Use

### After Creating Documentation
```bash
# You just created TESTING-GUIDE.md, README.md, etc.
/clean-context auto
```
**Result:** Quickly removes docs, frees 30-60k tokens

### During Long Sessions
```bash
# Context is at 65%, mixture of files
/clean-context
```
**Result:** Review all options, decide what to keep

### Just Checking Status
```bash
# Want to see what's using space without removing
/clean-context report
```
**Result:** See breakdown, plan cleanup for later

### Between Tasks
```bash
# Finished feature X, starting feature Y
/clean-context auto
```
**Result:** Clean slate for new work

## Safety Guarantees

✅ **Never automatic:** Always asks for confirmation
✅ **Only safe files:** Only suggests removing files saved to disk
✅ **Reversible:** Can re-read any file with Read tool
✅ **Transparent:** Shows exactly what will be removed and impact
✅ **Protective:** Never removes active work files

## Comparison with /compact

| Feature | /clean-context | /compact |
|---------|----------------|----------|
| **What it cleans** | Files (docs, plans) | Conversation history |
| **Savings** | 30-60k tokens | 5-15k tokens |
| **Reversible** | Yes (re-read files) | No (history compressed) |
| **Best for** | After creating docs | Long conversations |
| **Interactivity** | Shows what's removed | Automatic compression |

**When to use both:**
1. `/clean-context` first (remove docs/plans)
2. `/compact` second (compress conversation)
3. Result: Maximum context savings

## Tips for Success

### Tip 1: Run After Documentation
```bash
# Created 3 markdown files in this session
✅ Now: /clean-context auto
❌ Later: Forget and hit 80% context
```

### Tip 2: Review Before Auto Mode
```bash
# First time using the command
✅ First: /clean-context (review what it does)
✅ Later: /clean-context auto (once comfortable)
```

### Tip 3: Use Report to Plan
```bash
# Not ready to cleanup yet
✅ Check: /clean-context report
✅ Later: /clean-context when context > 60%
```

### Tip 4: Trust Safe Categories
```
If a file shows:
  ✅ SAFE TO REMOVE - Documentation Files

Then it's genuinely safe because:
  - File is saved to disk
  - Can re-read with: Read(file_path: "...")
  - Not modified recently
```

## Troubleshooting

### "I removed a file but need it back"
**Solution:** Use the Read tool
```bash
Read(file_path: "path/to/file.md")
```
The file still exists on disk, just not in context anymore.

### "Command removed a file I was working on"
**This shouldn't happen!** The skill protects active work files.

If it did:
1. Use Read tool to reload the file
2. Report this as a bug - active files should be marked 🔴 KEEP

### "Context is still high after cleanup"
**Possible reasons:**
- Large skills loaded - expected
- Long conversation history - try `/compact`
- Many active work files - expected when working

**Check:** Run `/clean-context report` to see breakdown

## Related Commands

- `/compact` - Compress conversation history
- `/help` - Get help with Claude Code
- `/clear` - Clear conversation history (starts fresh)

---

**Created:** 2025-12-14
**Version:** 1.0
**Skill:** context-management (~/.claude/skills/context-management/SKILL.md)
