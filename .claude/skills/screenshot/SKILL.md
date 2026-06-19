<!-- vendored-from: APS.JimClaudeCodeConfig/global/skills/screenshot/SKILL.md @ 6dfd2cf
     adapted-for: PinballWizard (adapted: work-item attachment section updated — this repo uses GitHub Issues, not Jira/Azure DevOps)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

---
name: screenshot
description: >-
  Find, read, and analyze screenshots from the configured screenshots directory
---

# Screenshot Skill

**Version:** 1.1
**Auto-Trigger:** Yes - activates when user mentions screenshots
**Project Scope:** All projects
**Purpose:** Automatically locate and read screenshot files for analysis
**Updated:** 2025-11-02 - Added PowerShell support for Windows environments

---

## Configuration

Set `SCREENSHOT_DIR` from `~/.claude/user.config.json` → `screenshots.directory`, or fall back to `$HOME/Pictures/Screenshots`.

```bash
SCREENSHOT_DIR=$(python -c "import json,os; c=json.load(open(os.path.expanduser('~/.claude/user.config.json'))); print(c.get('screenshots',{}).get('directory',''))" 2>/dev/null)
SCREENSHOT_DIR="${SCREENSHOT_DIR:-$HOME/Pictures/Screenshots}"
```

## Skill Purpose

Automatically finds and reads screenshot files when the user references them:
- **Screenshots Location:** `$SCREENSHOT_DIR` (configured in user.config.json)
- **Auto-detection:** Finds latest screenshot when user says "screenshot" or "screen shot"
- **Multi-screenshot:** Can handle references to multiple screenshots

**Eliminates manual path specification for screenshot analysis.**

---

## When This Skill Activates

### Automatic Triggers (Proactive)

1. **User says "check my screenshot"** or "see my screenshot"
2. **User says "latest screenshot"** or "most recent screenshot"
3. **User says "look at the screenshot"**
4. **User references "screenshot" in context** (e.g., "the gateway screenshot shows...")
5. **User says "check screenshots"** (plural - analyze multiple)

### Manual Triggers (User Request)

- User explicitly asks to "read screenshot"
- User provides partial filename and says "screenshot"
- User wants to compare multiple screenshots

---

## Screenshot Workflow

### Step 1: Detect Screenshot Reference

```bash
# User message patterns that trigger this skill:
- "check my screenshot"
- "latest screenshot"
- "see screenshot"
- "look at screenshot"
- "the screenshot shows"
- "in the screenshot"
```

### Step 2: Find Screenshot Files

**IMPORTANT: Use PowerShell on Windows (Git Bash has issues with paths)**

```powershell
# Screenshots location (FIXED PATH for this user)
$SCREENSHOT_DIR = "$SCREENSHOT_DIR"

# Find latest screenshot (PowerShell - PREFERRED on Windows)
$LATEST_SCREENSHOT = Get-ChildItem "$SCREENSHOT_DIR" -Filter *.png | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName

# Alternative: Include both PNG and JPG
$LATEST_SCREENSHOT = Get-ChildItem "$SCREENSHOT_DIR" -Include *.png,*.jpg | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName

# Find latest N screenshots (if user says "screenshots" plural)
$LATEST_5_SCREENSHOTS = Get-ChildItem "$SCREENSHOT_DIR" -Include *.png,*.jpg | Sort-Object LastWriteTime -Descending | Select-Object -First 5 -ExpandProperty FullName
```

**Using Bash tool to invoke PowerShell:**

```bash
# Find latest screenshot
LATEST_SCREENSHOT=$(powershell -Command "Get-ChildItem '$SCREENSHOT_DIR' -Filter *.png | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName")

# Find latest 5 screenshots
powershell -Command "Get-ChildItem '$SCREENSHOT_DIR' -Include *.png,*.jpg | Sort-Object LastWriteTime -Descending | Select-Object -First 5 | ForEach-Object { \$_.FullName }"
```

**Fallback: Bash commands (may not work reliably on Windows)**

```bash
# Screenshots location (FIXED PATH for this user)
SCREENSHOT_DIR="$SCREENSHOT_DIR"

# Find latest screenshot (bash - use only if PowerShell unavailable)
LATEST_SCREENSHOT=$(ls -t "$SCREENSHOT_DIR"/*.png "$SCREENSHOT_DIR"/*.jpg 2>/dev/null | head -1)

# Find latest N screenshots (if user says "screenshots" plural)
LATEST_5_SCREENSHOTS=$(ls -t "$SCREENSHOT_DIR"/*.png "$SCREENSHOT_DIR"/*.jpg 2>/dev/null | head -5)
```

### Step 3: Read and Analyze Screenshot

```bash
# Read the screenshot using Read tool
Read(file_path: $LATEST_SCREENSHOT)

# If multiple screenshots requested:
for screenshot in $LATEST_5_SCREENSHOTS; do
  Read(file_path: $screenshot)
done
```

### Step 4: Prompt to Attach to Work Item (AUTOMATIC)

**After reading any screenshot, ALWAYS prompt the user to attach it to the current work item.**

This repo (PinballWizard) tracks work via **GitHub Issues** on the `jkeeley2073/PinballWizard` repository.

```bash
# Check if there's an active work item
WORK_ITEM_CONTEXT=$(cat .work-item-context 2>/dev/null)

if [ -n "$WORK_ITEM_CONTEXT" ]; then
  # Parse GitHub issue number from context
  ISSUE_NUMBER=$(echo "$WORK_ITEM_CONTEXT" | grep -oP '#\K\d+')
  ISSUE_URL="https://github.com/jkeeley2073/PinballWizard/issues/$ISSUE_NUMBER"

  # Prompt user using AskUserQuestion tool
  echo "📎 Attach screenshot to GitHub Issue #$ISSUE_NUMBER?"

  # If user says yes, upload via gh CLI:
  # gh issue comment $ISSUE_NUMBER --body "![screenshot]($LATEST_SCREENSHOT)"
  # Or: attach as comment with image inline using gh api
else
  echo "ℹ️  No active work item - screenshot not attached"
  echo "   (To attach later, comment on the relevant GitHub Issue)"
fi
```

**Using AskUserQuestion Tool:**

```javascript
AskUserQuestion({
  questions: [{
    question: `Attach this screenshot to GitHub Issue #${ISSUE_NUMBER}?`,
    header: "Attach?",
    multiSelect: false,
    options: [
      {
        label: "Yes, attach screenshot",
        description: `Upload screenshot to ${ISSUE_URL} for documentation`
      },
      {
        label: "No, skip attachment",
        description: "I'll attach it manually later if needed"
      }
    ]
  }]
})
```

---

## User Interaction Flows

### Flow 1: Single Latest Screenshot

**User:** "check my latest screenshot"

**Assistant:**
```
Reading latest screenshot from $SCREENSHOT_DIR...

[Reads and displays screenshot: Screenshot 2025-11-02 003045.png]

I can see the pinwiz.ai Container App overview page showing:
- Resource: pinwiz-wizard-api
- Status: Running (green checkmark)
- Active revisions: 1/1 healthy

What would you like me to help you with regarding this Container App configuration?
```

### Flow 2: Multiple Screenshots

**User:** "compare the last 3 screenshots"

**Assistant:**
```
Reading last 3 screenshots from $SCREENSHOT_DIR...

[Reads Screenshot 2025-11-02 003045.png]
[Reads Screenshot 2025-11-02 002830.png]
[Reads Screenshot 2025-11-02 002615.png]

Comparing the three screenshots:
1. First screenshot (003045): Shows backend health as Healthy
2. Second screenshot (002830): Shows HTTP settings configuration
3. Third screenshot (002615): Shows routing rules

Would you like me to analyze specific differences or configurations?
```

### Flow 3: Screenshot in Context

**User:** "check my latest screenshot, that's what I see at the gateway and the backend pool is the new one"

**Assistant:**
```
Reading latest screenshot from $SCREENSHOT_DIR...

[Reads and analyzes screenshot]

I can see the Application Gateway shows the backend pool as healthy. Since you mentioned:
- App Service is healthy ✓
- Backend pool shows healthy ✓
- But Gateway returns 403...

This suggests a routing or configuration issue. Let me investigate the gateway routing rules and HTTP settings.
```

---

## Screenshot Naming Patterns

**Windows Screenshot Tool Formats:**
- `Screenshot YYYY-MM-DD HHMMSS.png` (default Windows Snipping Tool)
- `Screenshot (N).png` (numbered screenshots)
- Any `.png` or `.jpg` in the Screenshots folder

**Sorting:**
- Always sorted by modification time (newest first)
- Uses `ls -t` to get chronological order

---

## Advanced Features

### Find Screenshot by Partial Name

If user provides partial filename:

```bash
# User: "check screenshot 003045"
PARTIAL_NAME="003045"

# PowerShell approach (PREFERRED on Windows)
SCREENSHOT=$(powershell -Command "Get-ChildItem '$SCREENSHOT_DIR' -Include *.png,*.jpg | Where-Object { \$_.Name -like '*003045*' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName")

# Bash approach (fallback)
SCREENSHOT=$(ls -t "$SCREENSHOT_DIR"/*$PARTIAL_NAME*.png "$SCREENSHOT_DIR"/*$PARTIAL_NAME*.jpg 2>/dev/null | head -1)
```

### Find Screenshots by Time Range

```bash
# Find screenshots from last 5 minutes

# PowerShell approach (PREFERRED on Windows)
powershell -Command "Get-ChildItem '$SCREENSHOT_DIR' -Include *.png,*.jpg | Where-Object { \$_.LastWriteTime -gt (Get-Date).AddMinutes(-5) } | Sort-Object LastWriteTime -Descending | ForEach-Object { \$_.FullName }"

# Bash approach (fallback - may not work on Windows)
find "$SCREENSHOT_DIR" -name "*.png" -o -name "*.jpg" -mmin -5 | sort -r
```

### Handle Missing Screenshots

```bash
if [ -z "$LATEST_SCREENSHOT" ]; then
  echo "No screenshots found in $SCREENSHOT_DIR"
  echo "Please take a screenshot first or provide a different path."
  exit 1
fi
```

---

## Integration with Other Skills

### Works With: All Skills
- Any skill can reference screenshots for visual context
- E2E testing: Compare test results screenshots
- App troubleshooting: Blazor / API / gateway screenshots
- Code review: UI screenshots

### Common Scenarios

**Debugging:**
```
User: "check the error screenshot"
Assistant: [Reads latest screenshot, analyzes error message, suggests fix]
```

**Configuration Review:**
```
User: "is this gateway config correct? see screenshot"
Assistant: [Reads screenshot, validates configuration against best practices]
```

**Test Results:**
```
User: "test failed, see screenshot"
Assistant: [Reads screenshot, analyzes failure, suggests fix]
```

---

## Configuration

### Screenshot Directory (User-Specific)

```bash
# Path configured via user.config.json → screenshots.directory
SCREENSHOT_DIR="$SCREENSHOT_DIR"

# If supporting multiple users:
SCREENSHOT_DIR="C:\Users\$USERNAME\Pictures\Screenshots"
```

### Supported File Types

- PNG (primary format for Windows screenshots)
- JPG/JPEG (secondary format)
- BMP (if needed)

---

## Error Handling

### No Screenshots Found

```
ℹ️ No screenshots found in $SCREENSHOT_DIR

Please:
1. Take a screenshot using Windows + Shift + S
2. Save it to the Screenshots folder
3. Try again
```

### Permission Error

```
❌ Cannot access $SCREENSHOT_DIR

Check:
1. Folder exists
2. Proper permissions
3. Path is correct
```

### Multiple Screenshots Ambiguous

```
Found 12 screenshots from the last hour. Which one?

Latest 5:
1. Screenshot 2025-11-02 003045.png (2 minutes ago)
2. Screenshot 2025-11-02 003015.png (5 minutes ago)
3. Screenshot 2025-11-02 002945.png (7 minutes ago)
4. Screenshot 2025-11-02 002915.png (10 minutes ago)
5. Screenshot 2025-11-02 002845.png (12 minutes ago)

Should I use #1 (latest)?
```

---

## Benefits

- ✅ **No manual path entry** - Always knows where screenshots are
- ✅ **Auto-detection** - Finds latest screenshot automatically
- ✅ **Context-aware** - Understands screenshot references in conversation
- ✅ **Multi-screenshot support** - Can analyze multiple screenshots
- ✅ **Time-sorted** - Always gets the most recent first

---

## Quick Reference

**User Says** → **Skill Action**
- "check screenshot" → Read latest screenshot
- "latest screenshot" → Read most recent PNG/JPG
- "compare screenshots" → Read latest 3-5 screenshots
- "screenshot shows X" → Read latest, analyze for X
- "in the screenshot" → Read latest, provide context

**Just mention "screenshot" and the skill handles the rest!**
