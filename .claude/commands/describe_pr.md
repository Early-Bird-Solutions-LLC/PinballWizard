---
description: Generate comprehensive PR descriptions following the repository PR template
---
<!-- vendored-from: APS.JimClaudeCodeConfig/global/commands/describe_pr.md @ 6dfd2cf
     adapted-for: PinballWizard (adapted: removed humanlayer thoughts sync/thoughts/ path references; uses gh CLI; PR description written inline or to .superpowers/prs/; no work-item linking)
     last-synced: 2026-06-19 — drift: scripts/check_claude_config_drift.py -->

# Generate PR Description

You are tasked with generating a comprehensive pull request description following the repository's standard template and the PR self-audit defined in `.claude/PR-AUDIT.md`.

## Steps to follow:

1. **Read the PR self-audit checklist:**
   - Read `.claude/PR-AUDIT.md` to understand the checklist requirements
   - Read `.claude/skills/pr/SKILL.md` for formatting requirements

2. **Identify the PR to describe:**
   - Check if the current branch has an associated PR: `gh pr view --json url,number,title,state 2>/dev/null`
   - If no PR exists for the current branch, or if on main, list open PRs: `gh pr list --limit 10 --json number,title,headRefName,author`
   - Ask the user which PR they want to describe

3. **Check for existing description:**
   - Check if `.superpowers/prs/{number}_description.md` already exists
   - If it exists, read it and inform the user you'll be updating it
   - Consider what has changed since the last description was written

4. **Gather comprehensive PR information:**
   - Get the full PR diff: `gh pr diff {number}`
   - If you get an error about no default remote repository, instruct the user to run `gh repo set-default` and select the appropriate repository
   - Get commit history: `gh pr view {number} --json commits`
   - Review the base branch: `gh pr view {number} --json baseRefName`
   - Get PR metadata: `gh pr view {number} --json url,title,number,state`

5. **Analyze the changes thoroughly:** (ultrathink about the code changes, their architectural implications, and potential impacts)
   - Read through the entire diff carefully
   - For context, read any files that are referenced but not shown in the diff
   - Understand the purpose and impact of each change
   - Identify user-facing changes vs internal implementation details
   - Look for breaking changes or migration requirements

6. **Handle verification requirements:**
   - For each PR-AUDIT checklist item:
     - If it's a command you can run (like `dotnet build`, `dotnet test`, etc.), run it
     - If it passes, mark the checkbox as checked: `- [x]`
     - If it fails, keep it unchecked and note what failed: `- [ ]` with explanation
     - If it requires manual testing (UI interactions, live Azure), leave unchecked and note for user
   - Document any verification steps you couldn't complete

7. **Generate the description:**
   - Use the standard PinballWizard PR format from `.claude/skills/pr/SKILL.md`:
     ```markdown
     ## Summary
     - [Change bullet 1]
     - [Change bullet 2]

     ## Test plan
     - [ ] Local build passes (`dotnet build`)
     - [ ] Relevant tests pass (`dotnet test`)
     - [ ] [Manual step if needed]
     ```
   - Be specific about problems solved and changes made
   - Focus on the "why" as much as the "what"
   - Include technical details in appropriate sections

8. **Save and update the PR:**
   - Write the completed description to `.superpowers/prs/{number}_description.md`
   - Update the PR description directly: `gh pr edit {number} --body-file .superpowers/prs/{number}_description.md`
   - Confirm the update was successful
   - Ensure the `claude-code` label is applied: `gh pr edit {number} --add-label claude-code`
   - If any verification steps remain unchecked, remind the user to complete them before merging

## Important notes:
- Be thorough but concise - descriptions should be scannable
- Focus on the "why" as much as the "what"
- Include any breaking changes or migration notes prominently
- If the PR touches multiple components, organize the description accordingly
- Always attempt to run verification commands when possible
- Clearly communicate which verification steps need manual testing
- Check `.claude/PR-AUDIT.md` for PinballWizard-specific audit items (provenance, Cosmos surface, User-Delight surface, community-resource posture, etc.)
