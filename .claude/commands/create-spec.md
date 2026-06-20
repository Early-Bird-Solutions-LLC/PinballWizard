---
description: Create a lightweight specification for a feature or change
model: sonnet
---
<!-- vendored-from: APS.JimClaudeCodeConfig/global/commands/create-spec.md @ 6dfd2cf
     adapted-for: PinballWizard (adapted: removed DRS-XXXXX/AB#XXX work item references; spec path uses docs/specs/ instead of thoughts/specs/)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

# Create Specification

You are creating a specification document for a feature or change using the spec-driven development workflow.

## When Invoked

1. **If parameters provided** (e.g., `/create-spec add dark mode toggle`):
   - Use the parameter as the feature name/description
   - Skip initial prompting

2. **If no parameters**, ask:
   ```
   I'll help you create a specification. What are we building?

   Please describe:
   1. The feature or change
   2. Any constraints or requirements
   3. The GitHub issue number (if applicable)
   ```

## Process

### Step 1: Gather Requirements

Ask clarifying questions until you understand:
- What problem does this solve?
- What does success look like?
- What's out of scope?

### Step 2: Research (if needed)

If implementation touches existing code:
```
Let me research the current implementation...
```
Use Explore agent to understand existing patterns.

### Step 3: Write Spec

Create file at `docs/specs/{feature-name}.md` using this template:

```markdown
# Spec: [Feature Name]

**Created:** [Date]
**GitHub Issue:** [#XXX or N/A]
**Status:** Draft

## Objective

[1-2 sentences: What does this achieve?]

## Success Criteria

- [ ] Criterion 1
- [ ] Criterion 2
- [ ] Criterion 3

## Files to Modify

| File | Changes |
|------|---------|
| [path](path) | Description |

## Implementation Notes

[Constraints, patterns, edge cases]

## Out of Scope

[What this does NOT include]

## Validation Steps

1. Step 1
2. Step 2
```

### Step 4: Review

Present spec to user:
```
Here's the draft spec. Does this capture what we're building?

[Show spec content]

Ready to approve? (yes/no/edit)
```

If user approves, update status to "Approved".

## Output

- Spec file: `docs/specs/{feature-name}.md`
- Confirm: "Spec created at docs/specs/{name}.md - ready for /implement-spec"
