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
- `Trip` → `TripStop`; spend comes from `Transaction` (Plaid) + `Expense` (manual)
- **`transactions`** (migration 004) = raw Plaid mirror, **source of truth** for bank spend.
  Every synced Plaid delta lands here regardless of trips; `trip_id` is NULLABLE (NULL until a
  trip claims it by date range or manual assignment). Ingestion is decoupled from trips.
- **`expenses`** = MANUAL entries only, going forward. (Legacy Plaid `expenses` rows are moved
  into `transactions` and deleted by migration 004.)
- **Trip Plaid spend** = `SUM(transactions WHERE trip_id = trip AND deleted_at IS NULL)`;
  **total trip spend** adds manual `expenses`. Both entities implement `ISpendLine` so
  `BudgetCalculationService` sums them uniformly.
- Assignment: `TripController` POST/PUT auto-assign unassigned, non-reviewed transactions whose
  date falls in `[start_date, end_date]`; `PATCH /api/transactions/{id}/assign` is the manual
  override (sets `reviewed=true` so auto-assign never moves it again). `reviewed=true` =
  user-confirmed; never auto-reassigned. `GET /api/transactions/unassigned` feeds the review queue.
- **Client data flow**: the browser reads `transactions` directly (read-only RLS, migration 004)
  for trip spend and review; every write goes through the API.
  - `Dashboard.jsx` sums `transactions(amount)` (all assigned rows) + reviewed manual `expenses`.
  - `TripDetail.jsx` shows a merged Plaid + manual feed. Spend matches the Dashboard: all assigned
    transactions + reviewed manual expenses. Plaid rows are immutable — "remove from trip" =
    `PATCH assign {trip_id:null}`, "confirm" = `PATCH assign {trip_id:this}`. The adjust/skip
    (`adjusted_amount`) flow is manual-expense-only. The Expenses "Reset" button re-pulls via
    `POST /api/plaid/reset-cursor` then `POST /api/plaid/sync?trip_id=` — it no longer deletes
    anything.
  - `TransactionReview.jsx` reviews this trip's unreviewed transactions: confirm =
    `PATCH assign {trip_id:this}`, "not a trip expense" = `PATCH assign {trip_id:null}` (unassign,
    not a $0 skip). Category is shown read-only (auto-derived; not browser-editable).
- **`POST /api/plaid/reset-cursor`** clears `sync_cursor` on the user's connections so the next
  sync re-pulls full Plaid history. UserId-filtered; never deletes stored transactions (dedup on
  `plaid_transaction_id` prevents duplicates). Replaces the old client-side "delete plaid expenses
  + null last_sync_at" reset.
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
| ✅ Resolved | No active trips = silent black hole — obsolete: sync now stores every transaction in `transactions` (unassigned, `trip_id` NULL) even with no trips; `SyncResult.Warning` only notes there was nothing to auto-assign to |
| ✅ Resolved | Transactions with no matching trip lost past the cursor — **fixed by the `transactions` table (migration 004)**. Ingestion is decoupled from trips: every delta is stored regardless of trip match, `trip_id` left NULL until claimed. Nothing is ever skipped past the cursor, so no data loss. Unassigned rows are claimed later via auto-assign (trip create/date change) or manual assignment |
| ⚠️ Low | Timezone drift — 2-day buffer mostly covers it |

## Security & Correctness Issues (Fix Before Go-Live)

| Priority | Issue | File | Fix |
|---|---|---|---|
| ✅ Resolved | Webhook had no Plaid signature validation — every webhook now requires a valid `Plaid-Verification` ES256 JWT (key fetched from `/webhook_verification_key/get` by `kid`, cached in-memory; signature + `iat` freshness + `request_body_sha256` body hash all checked, 401 otherwise) | `PlaidWebhookVerifier.cs`, `PlaidController.cs` | |
| ✅ Resolved | Webhook fell back to `SyncAllAsync()` if item_id lookup failed — unknown item_id now logs + returns `Ok()`; `SyncAllAsync` has been deleted entirely so no sync-all is reachable from anywhere | `PlaidController.cs`, `TransactionSyncService.cs` | |
| ✅ Resolved | Browser could SELECT `access_token`/`item_id`/`sync_cursor` from `plaid_connections` (RLS limits rows, not columns) — table grants revoked from `anon`/`authenticated`; column-level SELECT re-granted on safe columns only. Apply `003_lock_down_plaid_connections.sql` in the Supabase SQL editor | `client/supabase/migrations/003_lock_down_plaid_connections.sql` | |
| ✅ Resolved | Swagger + `/` → Swagger redirect exposed in production — Swagger now Development-only; `/` returns a plain 200 health response | `Program.cs` | |
| ✅ Resolved | JWT validation accepted any issuer/audience — now pinned to `{Supabase:Url}/auth/v1` and audience `authenticated` | `Program.cs` | |
| ✅ Resolved | No rate limiting — `/api/plaid/*` limited to 10 req/min per user, webhook to 30 req/min per IP, 429 on excess | `Program.cs`, `PlaidController.cs` | |
| ✅ Resolved | CORS hardcoded to localhost — now driven by `Cors:AllowedOrigins` config. Production: set `Cors__AllowedOrigins__0=https://your-frontend-url` | `Program.cs` | |
| ✅ Resolved | `ExchangeToken` missing `UserId` filter — query now includes `&& c.UserId == UserId`, so one user can't overwrite another's connection | `PlaidController.cs` | |
| ✅ Resolved | Null `TransactionId` fallback generated a random GUID — transactions without a stable ID are now skipped with a warning log | `PlaidService.cs` | |
| ✅ Resolved | No logging in sync services — structured logging added at sync start/end and on skip/warning paths | `TransactionSyncService.cs`, `PlaidService.cs` | |
| ✅ Resolved | No pagination (truncated at 500) — `/transactions/sync` loops on `has_more` until the full delta set is fetched | `PlaidService.cs` | |
| ✅ Resolved | `BufferDays`/`MaxDays` duplicated as local constants — promoted to class-level constants | `TransactionSyncService.cs` | |
| LOW | `ExchangeRequest`/`WebhookPayload` records defined inside controller file | `PlaidController.cs` | Move to `Contracts/` folder |

## Engineering Notes (Deep Context)

### Pending → Settled Duplicate Problem (RESOLVED)
Plaid assigns a different `transaction_id` to a pending transaction vs the same transaction once it settles. Ingestion now uses `/transactions/sync` (`added/modified/removed` deltas). Pending transactions ARE imported. When one settles, Plaid sends `added` (new ID, with `pending_transaction_id` pointing at the old one) + `removed` (old pending ID): the added-handler updates the existing pending row in place and swaps its Plaid ID to the settled one, so pending + settled never coexist. `removed` deltas soft-delete (`transactions.deleted_at`) — a global EF query filter hides soft-deleted rows everywhere; sync dedup uses `IgnoreQueryFilters()` so removed transactions are never re-imported. (This now operates on the `transactions` table, not `expenses`.)

### Timezone Drift
Plaid returns date-only strings (no time). Server uses `DateTime.UtcNow`. For users in UTC-5 to UTC-8, a transaction at 10 PM local appears as next calendar day in UTC — can miss trip date windows by one day. The existing 2-day buffer mostly covers this.

### Sync Logic (cursor-based, writes to `transactions`)
- Ingestion is cursor-based via `/transactions/sync` and writes to the **`transactions`** table (NOT `expenses`) — `TransactionSyncService`
- Cursor stored per connection in `plaid_connections.sync_cursor` (migration `002_transactions_sync.sql`); persisted in the same `SaveChanges` as the applied deltas so it never advances past unstored data
- First sync (`sync_cursor` NULL): Plaid returns full available history as `added`
- **No trip gating**: every delta is stored regardless of trips. `added` → insert (dedup on `plaid_transaction_id` via `IgnoreQueryFilters` so soft-deleted rows still block reimport), `trip_id` set only if a planning/active trip window contains the date else NULL; `modified` → update in place; pending→settled swaps the row's Plaid ID in place (never both versions); `removed` → soft-delete (`deleted_at`). Credits (amount < 0) are skipped.
- If a user has no planning/active trips, the sync STILL runs and stores everything unassigned; `SyncResult.Warning` just notes nothing was auto-assigned. No black hole, no data loss.
- Trip-specific sync (`POST /api/plaid/sync?trip_id=`): runs the normal cursor sync routing in-window transactions to that trip, then claims existing unassigned / other-trip **non-reviewed** transactions in the trip window (start−2 … min(end+2, today), 90-day cap). Reviewed rows are never moved.
- Assignment lives in `TransactionAssignmentService` (auto-assign on trip create/update via `TripController`; manual via `TransactionController`).
- Free-tier 50-txn cap: enforced in `TransactionAssignmentService` (auto-assign + manual PATCH). The sync-time trip routing (`MatchTrip` during ingestion) is still UNCAPPED — a `TODO` in `TransactionSyncService` marks where to gate the assigned `trip_id` (never the insert itself; ingestion is never capped).
- Dedup: global `plaid_transaction_id` unique index (migration 004) + in-memory lookup (including soft-deleted rows) prevents re-inserting

### Trip Matching Order
`FindMatchingTrip` uses `FirstOrDefault` over trips ordered by `StartDate ASC`. If two trips overlap, earliest-starting trip wins. This is deterministic now — it wasn't before the `OrderBy` fix.

## Monetization / Fraud Prevention

| Rule | Where enforced | Notes |
|---|---|---|
| Plan source of truth = `subscriptions` table, read server-side by JWT UserId | `PlanService` (Infrastructure) via `IPlanService` | No row or non-active status → free. Client localStorage (`plan.js`) is display cache ONLY — never trusted |
| Free plan: trips max 7 days (inclusive; exactly 7 allowed) | `PlanPolicy.IsTripDurationAllowed` + `TripController` POST/PUT `/api/trips` | Rejects with **402 Payment Required** + `code:"upgrade_required"`. PUT gates the RESULTING dates so trips can't be stretched field-by-field |
| Free plan: max **50** transactions ASSIGNED per trip (`FreePlanLimits.MaxAssignedTransactionsPerTrip`) | `TransactionAssignmentService` — auto-assign (trip create/date change) AND manual `PATCH /api/transactions/{id}/assign` | Auto-assign fills remaining slots oldest-first, stops, returns `AutoAssignResult.Warning`; manual assign into a full trip → `AssignOutcome.CapReached` → 402. Existing assignments are NEVER removed to make room; unassign/re-confirm are never capped. Pro uncapped. Counts trip-ASSIGNED rows only — unassigned Plaid rows don't burn quota. Sync-time trip routing is still uncapped (TODO in `TransactionSyncService`) |
| Trip create/update goes through the API, not direct Supabase inserts | `TripNew.jsx` → `POST /api/trips`, `TripDetail.jsx` date edits → `PUT /api/trips/{id}` | 402 shows upgrade CTA in the UI; POST also accepts `budgets` + `total_miles` so the client never inserts trips directly |
| Client reads plan from `GET /api/account/plan` | `AccountController` + `fetchPlan()` in `client/src/lib/plan.js` | localStorage kept as display cache while the fetch is in flight |
| 2FA (TOTP via Supabase Auth MFA): optional at signup, REQUIRED before connecting a bank | Client: `ConnectBank.jsx` gate (`isBankReady()` = verified TOTP factor + aal2). Server: `PlaidController.ExchangeToken` requires JWT `aal` claim == `aal2`, else **403 mfa_required** | Enroll/verify UI: `MfaSetup.jsx` (Account → Security + ConnectBank gate). Login challenge in `Login.jsx`. Helpers in `client/src/lib/mfa.js` |
| Supabase dashboard prerequisite | — | MFA (TOTP) must be enabled in Supabase Auth settings for enrollment to work |

Rules of thumb:
- NEVER enforce plan limits client-side only — every limit needs a server check keyed off the JWT.
- Plan rules live in `PlanPolicy`/`FreePlanLimits` (Core, pure, tested) — services call them, never duplicate the math.
- Hitting a limit must never delete or mutate existing user data — reject/skip + surface a message.

## Environment Variables
Load secrets via `dotnet user-secrets` locally, env vars in production. See `appsettings.json` for structure.
