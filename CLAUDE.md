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
| ✅ Resolved | Overlapping trips arbitrary order — `FindMatchingTrip` now sorts by `StartDate` internally, deterministic regardless of caller |
| ✅ Resolved | Date range backward sync — obsolete: cursor-based `/transactions/sync` replaced date windows |
| ✅ Resolved | Pending → settled reconciliation — migrated to `/transactions/sync`; pending imported, settled version updates the row in place (ID swap), never both |
| ✅ Resolved | No active trips = silent black hole — sync now skips (without advancing the cursor) and returns `SyncResult.Warning` |
| ⚠️ Open | Transactions with no matching trip are skipped AND the cursor advances past them — they will not be re-imported later (pre-migration they were re-fetchable by date). Trip-specific sync reassigns existing rows but cannot recover skipped ones |
| ⚠️ Low | Timezone drift — 2-day buffer mostly covers it |

## Security & Correctness Issues (Fix Before Go-Live)

| Priority | Issue | File | Fix |
|---|---|---|---|
| CRITICAL | Webhook has no Plaid signature validation — anyone can POST and trigger a sync | `PlaidController.cs` | Validate `Plaid-Verification` header before processing (approach needs a decision — JWT verification via Plaid `/webhook_verification_key/get`) |
| ✅ Resolved | Webhook fell back to `SyncAllAsync()` if item_id lookup failed — now returns `Ok()` early when the connection is not found; never syncs all users from an unauthenticated request | `PlaidController.cs` | |
| ✅ Resolved | CORS hardcoded to localhost — now driven by `Cors:AllowedOrigins` config. Production: set `Cors__AllowedOrigins__0=https://your-frontend-url` | `Program.cs` | |
| ✅ Resolved | `ExchangeToken` missing `UserId` filter — query now includes `&& c.UserId == UserId`, so one user can't overwrite another's connection | `PlaidController.cs` | |
| ✅ Resolved | Null `TransactionId` fallback generated a random GUID — transactions without a stable ID are now skipped with a warning log | `PlaidService.cs` | |
| ✅ Resolved | No logging in sync services — structured logging added at sync start/end and on skip/warning paths | `TransactionSyncService.cs`, `PlaidService.cs` | |
| ✅ Resolved | No pagination (truncated at 500) — `/transactions/sync` loops on `has_more` until the full delta set is fetched | `PlaidService.cs` | |
| ✅ Resolved | `BufferDays`/`MaxDays` duplicated as local constants — promoted to class-level constants | `TransactionSyncService.cs` | |
| LOW | `ExchangeRequest`/`WebhookPayload` records defined inside controller file | `PlaidController.cs` | Move to `Contracts/` folder |

## Engineering Notes (Deep Context)

### Pending → Settled Duplicate Problem (RESOLVED)
Plaid assigns a different `transaction_id` to a pending transaction vs the same transaction once it settles. Ingestion now uses `/transactions/sync` (`added/modified/removed` deltas). Pending transactions ARE imported. When one settles, Plaid sends `added` (new ID, with `pending_transaction_id` pointing at the old one) + `removed` (old pending ID): the added-handler updates the existing pending row in place and swaps its Plaid ID to the settled one, so pending + settled never coexist. `removed` deltas soft-delete (`expenses.deleted_at`) — a global EF query filter hides soft-deleted rows everywhere; sync dedup uses `IgnoreQueryFilters()` so removed transactions are never re-imported.

### Timezone Drift
Plaid returns date-only strings (no time). Server uses `DateTime.UtcNow`. For users in UTC-5 to UTC-8, a transaction at 10 PM local appears as next calendar day in UTC — can miss trip date windows by one day. The existing 2-day buffer mostly covers this.

### Sync Logic (cursor-based)
- Ingestion is cursor-based via `/transactions/sync` — no more date windows for fetching
- Cursor stored per connection in `plaid_connections.sync_cursor` (migration `002_transactions_sync.sql`); persisted in the same `SaveChanges` as the applied deltas so it never advances past unstored data
- First sync (`sync_cursor` NULL): Plaid returns full available history as `added`
- If a user has no planning/active trips, the sync is skipped WITHOUT advancing the cursor (otherwise deltas would be dropped forever) and `SyncResult.Warning` is set
- Trip-specific sync (`POST /api/plaid/sync?trip_id=`): runs the normal cursor sync routing in-window transactions to that trip, then reassigns existing Plaid expenses within the trip window (start−2 … min(end+2, today), 90-day cap) from other trips
- Dedup: `plaid_transaction_id` unique index + in-memory lookup (including soft-deleted rows) prevents re-inserting

### Trip Matching Order
`FindMatchingTrip` uses `FirstOrDefault` over trips ordered by `StartDate ASC`. If two trips overlap, earliest-starting trip wins. This is deterministic now — it wasn't before the `OrderBy` fix.

## Environment Variables
Load secrets via `dotnet user-secrets` locally, env vars in production. See `appsettings.json` for structure.
