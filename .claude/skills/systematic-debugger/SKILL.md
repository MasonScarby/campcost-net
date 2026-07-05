---
name: systematic-debugger
description: Root cause debugging workflow for CampCost. Use before proposing any fix. Reproduces the issue, identifies root cause with evidence, proposes the minimal safe fix, and recommends tests.
---

# Systematic Debugger

When invoked, follow this workflow exactly. Do not skip steps.

## Workflow

1. **Understand expected behavior** — what should happen?
2. **Reproduce the issue** — identify the exact input/state that triggers it
3. **Form hypotheses** — list 2-3 possible causes before looking at code
4. **Gather evidence** — read relevant code, logs, and data to confirm or rule out each hypothesis
5. **Identify root cause** — state it clearly: "The bug is X because Y"
6. **Propose the minimal fix** — the smallest change that corrects the behavior without side effects
7. **Check for regressions** — what else could this fix break?
8. **Recommend tests** — what test would have caught this, and what test should be added now?

## Rules

- Never guess without evidence — state what you looked at and what it showed
- Never rewrite large sections to fix a small bug
- Explain *why* the bug occurred, not just what it was
- If you cannot reproduce it, say so — don't invent a fix
- If multiple root causes are possible, list them ranked by likelihood before picking one

## Output Format

```
EXPECTED: [what should happen]
ACTUAL: [what is happening]
ROOT CAUSE: [the specific reason]
FIX: [minimal change]
REGRESSION RISK: [what to watch]
TESTS NEEDED: [what to write]
```
