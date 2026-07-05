---
name: financial-integrity
description: Audit any change touching Plaid, transactions, budgets, or money in CampCost. Automatically checks for duplicate transactions, decimal precision, idempotency, budget correctness, and soft-delete safety.
---

# Financial Integrity Auditor

Invoke whenever touching: Plaid integration, transaction sync, budget calculations, expense creation/deletion, or any money-related logic.

## Checklist — Run Every Time

### Duplicate Detection
- [ ] Is `plaid_transaction_id` uniqueness enforced at the DB level?
- [ ] Could this code path create the same transaction twice?
- [ ] Are pending AND posted versions of the same charge both being imported?

### Pending vs Posted
- [ ] Are pending transactions being skipped until settlement?
- [ ] Is the Plaid transaction ID stable (settled) before storing?

### Decimal Precision
- [ ] Is all money stored and calculated as `decimal`, not `float` or `double`?
- [ ] Are there any implicit conversions that could introduce rounding?

### Idempotency
- [ ] If this webhook or sync runs twice, is the result the same?
- [ ] Is there a guard against re-processing the same Plaid event?

### Budget Recalculation
- [ ] Is the budget recalculated from source data, not from a cached total?
- [ ] Does deleting/skipping an expense correctly update the budget?

### Soft Delete Safety
- [ ] Are we soft-deleting (`reviewed=true, adjusted_amount=0`) rather than hard-deleting?
- [ ] Does historical reporting still work correctly after a soft delete?

### Total Validation
- [ ] Do totals across all sources (Plaid + manual) add up correctly before and after this change?

## Output Format

```
FINANCIAL RISK ASSESSMENT
Duplicates: [pass / risk - explain]
Pending/Posted: [pass / risk - explain]
Decimal Precision: [pass / risk - explain]
Idempotency: [pass / risk - explain]
Budget Correctness: [pass / risk - explain]
Soft Delete Safety: [pass / risk - explain]
Total Validation: [pass / risk - explain]

ISSUES FOUND: [list or "none"]
RECOMMENDED FIXES: [list or "none"]
```
