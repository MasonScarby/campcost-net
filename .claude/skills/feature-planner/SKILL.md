---
name: feature-planner
description: Plan any CampCost feature before writing code. Analyzes problem, impact, DB/API/UI changes, edge cases, security, rollback, tests, and documentation needs. Run before any new feature implementation.
---

# Feature Planner

Run before writing any new feature code. Answer every question. Unknown answers are risks — say so rather than guessing.

## Questions to Answer

### Problem
1. What problem are we solving?
2. Who benefits and how?
3. Is there a simpler solution we're overlooking?

### Technical Impact
4. Which files will likely change?
5. Database changes? (new tables, columns, indexes, migrations)
6. API changes? (new endpoints, modified contracts, breaking changes)
7. UI changes? (new pages, components, state)

### Risk
8. What edge cases exist?
9. Security implications? (auth, RLS, PII, financial data)
10. What existing features could this break?
11. Rollback strategy if this needs to be reverted?

### Quality
12. Testing strategy? (unit, integration, E2E)
13. Documentation updates needed? (README, API docs, changelog)

## Output Format

```
FEATURE: [name]
PROBLEM: [what and who]
SIMPLER ALTERNATIVE: [or "none identified"]

FILES LIKELY CHANGING:
- [list]

DB IMPACT: [describe or "none"]
API IMPACT: [describe or "none"]
UI IMPACT: [describe or "none"]

EDGE CASES: [list]
SECURITY: [risks or "none"]
REGRESSION RISK: [what could break]
ROLLBACK PLAN: [how to undo]

TESTS NEEDED: [list]
DOCS NEEDED: [list]

READY TO CODE: [yes / no - explain blockers]
```
