---
name: documentation-architect
description: Keep CampCost documentation accurate and synchronized after any significant change. Updates README, API docs, architecture docs, DB schema docs, changelog, and environment variables. Docs must reflect actual code, not intended design.
---

# Documentation Architect

Trigger after any feature implementation, bug fix, or significant change.

## What to Update

### README
- [ ] Does the feature list still reflect what's built?
- [ ] Are setup instructions still accurate?
- [ ] Are environment variables listed correctly?

### Architecture Docs
- [ ] Does the architecture description match the current layer structure?
- [ ] Are new services, repositories, or controllers documented?

### API Docs
- [ ] Are all endpoints documented with method, path, auth requirement, request/response shape?
- [ ] Were any endpoints added, removed, or changed by this work?

### Database Schema
- [ ] Are new tables or columns reflected?
- [ ] Are indexes and constraints documented?

### Changelog
- [ ] Add an entry: what changed, why, date

### Environment Variables
- [ ] Any new env vars needed? Are they documented?

## Rules
- Docs must reflect actual code — not intended design
- Never invent endpoints, fields, or behaviors that don't exist
- If unsure what a piece of code does → mark `TODO: verify` rather than guessing
- Keep language simple — docs are for future-you and collaborators

## Output Format

```
DOCS REVIEW
README: [up to date / needs update - describe]
Architecture: [up to date / needs update - describe]
API Docs: [up to date / needs update - describe]
DB Schema: [up to date / needs update - describe]
Changelog: [entry added / needs entry]
Env Vars: [up to date / needs update - describe]

CHANGES MADE: [list]
TODO items: [anything left uncertain]
```
