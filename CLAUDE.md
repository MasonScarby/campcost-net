# CampCost .NET — Living Architecture Document

## Stack
- ASP.NET Core 8
- React (Vite)
- Supabase (PostgreSQL + JWT RS256)
- Plaid API

## Architecture
Preferred, not strict law — don't block future refactors over layering purity.

- **Core** — entities, domain services, interfaces. No EF Core, no Plaid, no HTTP.
- **Infrastructure** — EF Core, Supabase, Plaid integrations, external services
- **Api** — controllers + middleware only. No business logic in controllers.

## Domain Model
- `Trip` → `TripStop` → `Expense`
- Expenses come from: Plaid transactions OR manual entry
- Never duplicate pending + posted transactions
- Budget must always be derived from source of truth — no stale cache. Caching only allowed for performance if invalidation is guaranteed.
- All money uses `decimal` type

## Financial Rules (Critical)
- Pending vs posted must be reconciled — never import both
- Idempotency required for all Plaid webhooks
- Duplicate detection via `plaid_transaction_id` unique index
- Soft-delete flagged transactions only — do not hard delete financial history
- Skipped expenses: `reviewed=true, adjusted_amount=0, adjustment_note='skipped'`
- All calculations must be deterministic
- Validate totals across all sources before any changes

## Coding Rules
- `async/await` everywhere
- Nullable reference types enabled
- No business logic in controllers
- Consistent naming conventions

## Known Issues
| Status | Issue |
|---|---|
| ✅ Resolved | `trips[0]` random fallback — now returns null |
| ✅ Resolved | Overlapping trips arbitrary order — now `OrderBy(StartDate)` |
| ✅ Resolved | Date range backward sync — already handled in SyncConnections |
| ⚠️ Open | Pending → settled reconciliation — skip pending is a workaround, full `/transactions/sync` migration needed |
| ⚠️ Open | No active trips = silent black hole — needs warning in SyncResult |
| ⚠️ Low | Timezone drift — 2-day buffer mostly covers it |

## Security & Correctness Issues (Fix Before Go-Live)

| Priority | Issue | File | Fix |
|---|---|---|---|
| CRITICAL | Webhook has no Plaid signature validation — anyone can POST and trigger a sync | `PlaidController.cs:96` | Validate `Plaid-Verification` header before processing |
| CRITICAL | Webhook falls back to `SyncAllAsync()` if item_id lookup fails — syncs ALL users | `PlaidController.cs:114` | Return `Ok()` early if connection not found |
| ✅ Resolved | CORS hardcoded to localhost — now driven by `Cors:AllowedOrigins` config. Production: set `Cors__AllowedOrigins__0=https://your-app.vercel.app` | `Program.cs` | |
| HIGH | `ExchangeToken` missing `UserId` filter — could overwrite another user's connection | `PlaidController.cs:54` | Add `&& c.UserId == UserId` to query |
| HIGH | Null `TransactionId` fallback generates random GUID — breaks dedup permanently | `PlaidService.cs:57` | Skip or throw, never generate random ID |
| MED | No logging in sync services — can't debug production failures | `TransactionSyncService.cs` | Add structured logging at sync start/end |
| MED | No pagination on Plaid `/transactions/get` — truncates at 500 transactions | `PlaidService.cs` | Loop until all pages fetched |
| LOW | `BufferDays`/`MaxDays` defined as local constants in two places | `TransactionSyncService.cs` | Promote to class-level constants |
| LOW | `ExchangeRequest`/`WebhookPayload` records defined inside controller file | `PlaidController.cs` | Move to `Contracts/` folder |

## Engineering Notes (Deep Context)

### Pending → Settled Duplicate Problem
Plaid assigns a different `transaction_id` to a pending transaction vs the same transaction once it settles. Current workaround: skip all pending transactions entirely. Real fix: migrate from `/transactions/get` to `/transactions/sync` which returns `added/modified/removed` deltas and handles the ID swap natively.

### Timezone Drift
Plaid returns date-only strings (no time). Server uses `DateTime.UtcNow`. For users in UTC-5 to UTC-8, a transaction at 10 PM local appears as next calendar day in UTC — can miss trip date windows by one day. The existing 2-day buffer mostly covers this.

### Sync Window Logic
- First connection: 90 days back
- Incremental: `lastSyncAt - 2 days` OR earliest active trip start — whichever is earlier
- Hard cap: 90 days max
- Dedup: `plaid_transaction_id` unique index prevents re-inserting

### Trip Matching Order
`FindMatchingTrip` uses `FirstOrDefault` over trips ordered by `StartDate ASC`. If two trips overlap, earliest-starting trip wins. This is deterministic now — it wasn't before the `OrderBy` fix.

## Environment Variables
Load secrets via `dotnet user-secrets` locally, env vars in production. See `appsettings.json` for structure.
