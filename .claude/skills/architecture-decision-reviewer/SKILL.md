---
name: architecture-decision-reviewer
description: Prevent overengineering in CampCost. Reviews proposed architectural decisions for unnecessary complexity, duplicated abstractions, and inconsistency with existing patterns. Always asks: what is the simplest correct solution?
---

# Architecture Decision Reviewer

Run before introducing new patterns, abstractions, layers, or significant structural changes. Prefer simplicity over perfect architecture.

## Questions to Ask

### Is This Too Complex?
- What is the simplest solution that correctly solves the problem?
- Are we adding abstraction for abstraction's sake?
- Would a developer understand this in 6 months without a walkthrough?

### Is This Consistent?
- Does this match how similar things are already done in CampCost?
- Are we introducing a new pattern when an existing one would work?
- Does this fit the Core → Infrastructure → Api flow?

### Are We Duplicating?
- Does this functionality already exist somewhere?
- Are we wrapping something that could just be used directly?

### Is It Earning Its Complexity?
- What concrete problem does this abstraction solve?
- What is the cost of this layer vs the benefit?
- Could we start simpler and refactor when the need is proven?

### Consistency Check
- Does naming follow existing conventions?
- Does it belong in the correct layer?
- Are we pulling EF Core or Plaid into Core? (Not allowed)
- Are we putting business logic in a controller? (Not allowed)

## Output Format

```
DECISION: [what's being proposed]

SIMPLICITY CHECK: [is there a simpler approach?]
CONSISTENCY CHECK: [does it match existing patterns?]
DUPLICATION CHECK: [does this already exist?]
COMPLEXITY COST: [what are we adding and is it worth it?]

RECOMMENDATION: [proceed / simplify first / rethink]
SUGGESTED ALTERNATIVE: [if applicable]
```
