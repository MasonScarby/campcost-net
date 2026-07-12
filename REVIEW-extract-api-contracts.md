# Review Guide — `refactor/extract-api-contracts`

1 commit · 12 files · pure structural refactor · 127 tests passing (unchanged)
Delete this file after merging.

## The one idea
Six request DTOs were declared **inline at the bottom of controller files**, which
breaks the CLAUDE.md architecture rule ("Api = controllers + middleware only").
Moved all six into a new `Contracts/` folder under namespace `CampCost.Api.Contracts`.
**No behavior changed** — same records, same `[JsonPropertyName]` wire contract,
same fields. Only their *location* and *namespace* moved.

Why this is safe to review fast: if any wire contract or field had changed, the 127
tests (which construct `TripWriteRequest`/`ExchangeRequest` directly and assert
402/403/serialization behavior) would have failed. They didn't.

## Read in this order
1. **`src/CampCost.Api/Contracts/`** (new — the only new code)
   - `AccountContracts.cs` — `SetPlanRequest`
   - `PlaidContracts.cs` — `ExchangeRequest`, `WebhookPayload`
   - `TripContracts.cs` — `TripWriteRequest` (kept the `budgets`→total_budget comment)
   - `TransactionContracts.cs` — `AssignRequest`, `AdjustRequest`
   - The XML-free `//` comments carry the WHY (partial-update nullability, overlay-not-mutate, minimal webhook payload). Confirm they're accurate.
2. **The 4 controllers** — each lost its trailing `record` block, gained
   `using CampCost.Api.Contracts;`, and dropped the now-dead
   `using System.Text.Json.Serialization;` (Plaid keeps `using System.Text.Json;` — still uses `JsonSerializer`). Diffs are import + deletion only.
3. **2 test files** — `TripControllerPlanGateTests.cs`, `PlaidControllerAalTests.cs`
   gained `using CampCost.Api.Contracts;` (they build the DTOs directly). Nothing else.

## Decisions I made (say if you'd do differently)
- **Grouped by controller** (4 files) rather than one-file-per-record (6) or one big
  `Contracts.cs`. Middle ground: discoverable, not fragmented.
- **Left `WebhookPayload` minimal** (2 fields) — didn't expand it. It's a dispatch
  shape only; the security-critical parse happens in the verifier.
- Branched off `fix/trip-transactions-display` (not main) because `AdjustRequest`
  and the category param on `AssignRequest` only exist on that branch. **Review/merge
  that branch first, or this one carries its commits too.**

## Before merge
- `dotnet build` → 0 warnings (proves no orphaned usings)
- `dotnet test` → 127 green
- Untracked `graphify-out/` and `REVIEW-fix-trip-transactions-display.md` are NOT part of this change — leave them.
