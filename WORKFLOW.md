# CampCost.NET — Working Agreement

The rules for how Claude (and Mason) work in this repo. Agents must read this and
follow it. Mason reviews before anything reaches `main`.

## 1. Side branches only — `main` is protected
No agent commits to `main`. All work lands on a feature/fix branch. `main` only
advances when **Mason has read the diff and approved the merge**. A local
`pre-commit` hook blocks accidental direct commits to `main` (see `.githooks/`).

## 2. Every agent report ends with a Review Guide
Not just "what I did" — a guide for reviewing it:
- **Files to read, in order** (start here → then this → then tests)
- **Why each non-obvious decision was made** (the tradeoff, not just the change)
- **What to verify** and **what was deferred / is risky**

## 3. Mason codes the features; agents do grunt work
Agents are for tests, migrations, boilerplate, mechanical refactors. The
feature logic and design decisions are Mason's to write — that's the point of
the project (learning, interview readiness), not just shipping.

## 4. Reviewing an agent branch IS interview prep
Read every diff out loud as if explaining it to an interviewer: what changed,
why this approach, what the tradeoffs are, what you'd do differently. If you
can't explain a line, don't merge it — ask.

## Code style
Comments explain **why**, never what. A comment that restates the code is noise;
a comment that captures a constraint or a decision is worth keeping.
