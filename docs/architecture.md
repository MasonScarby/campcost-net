# CampCost Architecture

A layer-by-layer walkthrough of the backend, the frontend's role, and the full lifecycle of a Plaid transaction sync. Everything here describes the code as it exists — file paths are relative to the repo root.

## The Big Picture

CampCost is a three-project .NET solution plus a React SPA:

```
client (React SPA)
   │  Supabase JS: login, trip CRUD (RLS-protected)
   │  backendFetch(): Bearer JWT → API for Plaid + budget endpoints
   ▼
CampCost.Api ──────► CampCost.Core ◄────── CampCost.Infrastructure
(controllers,        (entities,             (EF Core → Supabase,
 auth, CORS,          domain services,       Plaid SDK,
 error handling)      interfaces)            sync orchestration)
```

Dependencies point inward only: Api and Infrastructure both reference Core; Core references nothing outside the base library. The Api project never references EF Core or the Plaid SDK directly — it gets everything through `AddInfrastructure()`.

The frontend talks to **two** backends: Supabase directly (auth, trip/stop/budget-category CRUD, protected by row-level security) and the .NET API (anything involving Plaid or budget math). The .NET API connects to Postgres with the service-role connection string, which bypasses RLS — intentional, because the API validates the JWT itself and scopes every query by the token's user ID.

## CampCost.Core

**Purpose:** the domain. Entities, business rules, and the interfaces the outer layers implement. No EF Core, no Plaid, no HTTP — every class here can be unit-tested with plain objects.

### Entities (`src/CampCost.Core/Entities/`)

| Entity | Table | Notes |
|---|---|---|
| `Trip` | `trips` | Name, destination, nullable start/end dates, `TotalBudget`, status (`planning` / `active` / `completed`). Navigation collections to expenses, budget categories, trip stops. Sync only considers planning/active trips. |
| `Expense` | `expenses` | The central financial record. `PlaidTransactionId` (nullable — manual expenses have none) is the dedup key. `Amount` is `decimal`, always positive (credits are skipped at sync time). `Category` is a string that must match the Postgres `expense_category` enum. `Source` is `plaid` or `manual`. |
| `PlaidConnection` | `plaid_connections` | One row per linked bank item: `AccessToken`, `ItemId`, institution name, `LastSyncAt` (null = never synced, triggers the 90-day initial pull). |
| `BudgetCategory` | `budget_categories` | Planned amount per category per trip. No `user_id` column — ownership flows through the parent trip. |
| `TripStop` | `trip_stops` | Planned route stops. Owned via the parent trip like `BudgetCategory`. |
| `Subscription` | `subscriptions` | Plan (`free` / `pro`) and status per user. Groundwork for Stripe; the frontend currently uses a localStorage dev switcher (`client/src/lib/plan.js`). |

Dates use `DateOnly` where the DB column is `date` (trip dates, expense dates) and `DateTime` for timestamps — Plaid reports transaction dates without a time component, so `DateOnly` avoids fake midnight timestamps.

### Domain services (`src/CampCost.Core/Services/`)

All three are stateless and registered as singletons.

**`TransactionCategorizationService`** — maps merchant text to a category. An ordered array of `(Regex, category)` rules is checked against `"{name} {merchantName}"`; first match wins, fallback is `misc`. The category strings must exactly match the Postgres `expense_category` enum values (`fuel`, `campground`, `food_groceries`, `gear`, `repairs`, `propane_utilities`, `activities`, `misc`) — the DB will reject anything else. Ported directly from the original Node backend's `categorize()` so behavior stayed identical across the rewrite.

**`TransactionMatchingService`** — decides which trip a transaction belongs to. Three steps:

1. **Date-window match:** the transaction date must fall within `[start_date - 2 days, min(end_date + 2 days, today)]`. The 2-day buffer absorbs late-posting transactions and timezone drift (Plaid returns date-only strings; the server compares against UTC "today"). The `today` cap stops a future trip's buffered window from swallowing past transactions.
2. **No-date fallback:** if no window matches, a trip with no `start_date` acts as a catch-all (the user created a trip but hasn't set dates yet).
3. **Null:** no match means the caller skips the transaction. It is never assigned to an arbitrary trip.

The caller passes trips ordered by `StartDate`, and the service takes the first match — so overlapping trips deterministically resolve to the earliest-starting one.

**`BudgetCalculationService`** — pure math, recomputed from source rows on every request:

```
planned_budget = sum(budget_categories.planned_amount)   if any category rows exist
               = trip.total_budget                        otherwise
money_left     = planned_budget - sum(expenses.amount)
daily_burn     = total_spent / max(days_since_start, 1)
days_left      = money_left / daily_burn                  (null if no spending yet)
```

`DaysLeftOnRoad` is deliberately nullable: with zero spending there is no burn rate, so the service returns null rather than a fake infinity/zero, and the UI can show "not enough data yet".

### Interfaces (`src/CampCost.Core/Interfaces/`)

**`IPlaidService`** — three methods: create a Link token, exchange a public token, fetch transactions for a date range. It returns `PlaidTransaction`, a small record `(TransactionId, Name, MerchantName, Amount, Date, Pending)` — the anti-corruption boundary that keeps Going.Plaid SDK types out of Core and out of the sync logic.

**`ITransactionSyncService`** — the orchestration contract: `SyncForUserAsync`, `SyncForTripAsync`, `SyncAllAsync`. Returns a `SyncResult` record (connections processed, transactions fetched, expenses upserted, expenses skipped). `SyncResult` also exposes a lowercase `synced` property aliasing `ExpensesUpserted` — kept for wire compatibility with the original Node backend's `{ synced: N }` response that the frontend still reads.

## CampCost.Infrastructure

**Purpose:** everything that touches the outside world — the database, the Plaid API — plus the sync orchestrator that stitches Core services together.

### `InfrastructureServiceExtensions` (`InfrastructureServiceExtensions.cs`)

One `AddInfrastructure(IConfiguration)` call registers the whole layer. Two details worth knowing:

- The Npgsql data source is built with `MapEnum<ExpenseCategoryEnum>("expense_category")`. Without this registration, Npgsql sends category values as `text` and Postgres rejects the insert (`column is of type expense_category but expression is of type text`). `ExpenseCategoryEnum` exists *only* for this type mapping — entities keep plain strings.
- `PlaidOptions` binds the `Plaid` config section (ClientId, Secret, Environment), and Going.Plaid's `PlaidClient` is a singleton over `IHttpClientFactory`.

### `CampCostDbContext` (`Data/CampCostDbContext.cs`)

EF Core context mapping PascalCase entities to the snake_case Supabase schema — every column is mapped explicitly in `OnModelCreating`. Key mapping details:

- `Expense.Category` and `BudgetCategory.Category` use a value converter: string ⇄ `ExpenseCategoryEnum` ⇄ Postgres enum.
- The dedup index: `(user_id, plaid_transaction_id)` unique, filtered `WHERE plaid_transaction_id IS NOT NULL`. Per-user rather than global, and the filter keeps manual expenses (null Plaid ID) out of the constraint.
- The context runs on the service-role connection, bypassing RLS. RLS still guards the Supabase client SDK path from the browser; the API enforces ownership in queries instead (`Where(t => t.UserId == UserId)`).

### `PlaidService` (`Services/PlaidService.cs`)

Thin adapter over the Going.Plaid SDK implementing `IPlaidService`:

- `CreateLinkTokenAsync` — requests a Link token for the Transactions product, US, English, attaching the configured webhook URL if present.
- `ExchangePublicTokenAsync` — swaps Link's short-lived public token for the permanent `(AccessToken, ItemId)` pair.
- `GetTransactionsAsync` — calls `/transactions/get` for a date range and projects each SDK transaction into the `PlaidTransaction` DTO. Amount keeps Plaid's convention: positive = money out, negative = credit.

Two known weaknesses live here (both tracked in `CLAUDE.md`): a null SDK `TransactionId` falls back to a random GUID, which would break dedup for that row; and there is no pagination loop, so windows with more than 500 transactions get truncated.

### `TransactionSyncService` (`Services/TransactionSyncService.cs`)

The heart of the system. Three entry points that share one core loop:

- `SyncForUserAsync(userId)` — loads that user's connections, delegates to the shared `SyncConnections` loop.
- `SyncAllAsync()` — same loop over every connection in the table (used by the webhook fallback).
- `SyncForTripAsync(userId, tripId)` — a different, trip-scoped algorithm (below).

**The shared loop (`SyncConnections`), per connection:**

1. **Load candidate trips** — the connection owner's `planning`/`active` trips, `OrderBy(StartDate)` so matching is deterministic.
2. **Compute the sync window** — start date is the *earlier* of `last_sync_at - 2 days` (incremental, catches late posters) and `earliest trip start - 2 days` (guarantees the whole trip is covered even if the bank was linked mid-trip). First-ever sync (null `last_sync_at`) starts 90 days back. Hard cap: never more than 90 days.
3. **Fetch transactions** from Plaid for that window.
4. **Load existing Plaid IDs** for the user into a HashSet.
5. **Per transaction:** skip credits (`Amount < 0`); skip pending (unstable ID — see below); skip already-imported IDs; match a trip via `TransactionMatchingService` and skip if none; otherwise insert an `Expense` with the categorized merchant, `Source = "plaid"`, `Reviewed = false`.
6. **Stamp `last_sync_at`** and save once per run.

**The trip-scoped variant (`SyncForTripAsync`):** fetches exactly the trip's own window (`start - 2` to `min(end + 2, today)`, 90-day cap) and routes *everything* to that trip — no matching step. It also builds a `plaid_transaction_id → existing expense` map first, and if a fetched transaction already exists **on a different trip**, it reassigns it. This is the repair tool for the "two overlapping trips and the wrong one got the transactions" case: the general sync gives overlaps to the earliest trip, and a trip-scoped sync lets the user explicitly claim them for another.

**Why pending transactions are skipped:** Plaid gives a pending transaction a temporary ID and issues a *different* permanent ID at settlement. Import both and you have a duplicate the unique index cannot catch (the IDs differ). Skipping pending means an expense only ever appears once, under its stable ID — at the cost of a day or two of lag. The proper fix is migrating from `/transactions/get` to `/transactions/sync`, which streams added/modified/removed deltas and handles the ID swap; that migration is planned.

## CampCost.Api

**Purpose:** HTTP concerns only. Controllers authenticate, extract the user, call a service, return the result — no business logic.

### `Program.cs`

- `AddInfrastructure(builder.Configuration)` — one line wires DB, Plaid, and all services.
- **JWT auth:** `Authority = {Supabase:Url}/auth/v1`, so ASP.NET fetches Supabase's JWKS and validates the RS256 signature. Lifetime is validated; issuer/audience checks are off (single known authority).
- **CORS:** origins come from the `Cors:AllowedOrigins` config array, defaulting to localhost dev ports. Production injects the Vercel URL via `Cors__AllowedOrigins__0`.
- **Swagger** with a Bearer security scheme, so protected endpoints are testable from `/swagger` by pasting a Supabase access token.
- Pipeline order: error middleware → CORS → authentication → authorization → controllers. `/` redirects to Swagger.

### `TripController` (`Controllers/TripController.cs`)

`[Authorize]`; user ID read from the `sub`/NameIdentifier claim. Three read endpoints:

- `GET /api/trips` — user's trips ordered by start date.
- `GET /api/trips/{id}/budget` — loads the trip with expenses + budget categories, hands them to `BudgetCalculationService`.
- `GET /api/trips/{id}/budget/by-category` — planned vs spent vs remaining per category, taking the union of categories that have a plan or have spending, so unplanned spending still shows up.

Every trip query filters by `UserId` — a valid JWT for user A can never read user B's trip (404, not 403, so IDs aren't confirmable).

### `PlaidController` (`Controllers/PlaidController.cs`)

- `POST /api/plaid/link-token` — returns `{ link_token }` for the frontend's Plaid Link modal.
- `POST /api/plaid/exchange-token` — exchanges the public token, then upserts `PlaidConnection` on `item_id` (re-linking the same bank refreshes the access token instead of duplicating the row). Request/response records use `JsonPropertyName` for snake_case fields (`public_token`).
- `POST /api/plaid/sync` — manual trigger; `?trip_id=` selects `SyncForTripAsync`, otherwise `SyncForUserAsync`.
- `POST /api/plaid/webhook` — `[AllowAnonymous]`. Ignores non-`TRANSACTIONS` webhook types, looks up the connection by `item_id`, and syncs just that user. **Two known pre-launch gaps:** the `Plaid-Verification` signature header is not yet validated (anyone can POST), and an unknown `item_id` currently falls back to `SyncAllAsync()` instead of no-oping. Both are tracked as CRITICAL in `CLAUDE.md`.

### `ErrorHandlingMiddleware` (`Middleware/ErrorHandlingMiddleware.cs`)

Global try/catch: `UnauthorizedAccessException` → 401 with message; anything else → 500, with the full exception text only in Development (production gets a generic message so internals never leak). All errors serialize to `{ "error": "..." }`.

## Client (`client/`)

React 19 + Vite SPA. Routing via React Router 7; all routes except `/login` are wrapped in `ProtectedRoute`, which checks the Supabase session from `AuthContext`.

- **`lib/supabase.js`** — Supabase client from `VITE_SUPABASE_URL` / `VITE_SUPABASE_ANON_KEY`.
- **`context/AuthContext.jsx`** — session state plus email/password and Google OAuth sign-in.
- **`lib/api.js`** — `backendFetch()`, a `fetch` wrapper that attaches the current Supabase access token as a Bearer header on every API call. This replaced the old pattern of trusting a `user_id` in the request body — identity now comes only from the verified JWT.
- **Pages** — `Dashboard` (trip list), `TripNew`, `TripDetail` (budget summary + category breakdown, Recharts), `TransactionReview` (review synced expenses), `ConnectBank` (`react-plaid-link` flow: link-token → Plaid modal → exchange-token → sync), `Account`.
- **`lib/plan.js`** — free/pro plan flag in localStorage; a dev stand-in until Stripe writes the `subscriptions` table.

Trip CRUD goes straight to Supabase (RLS-enforced); Plaid and budget endpoints go through `backendFetch` to the API.

## End-to-End: A Plaid Sync

What happens from trigger to updated budget:

```
Trigger (one of):
  • User clicks "Sync" → POST /api/plaid/sync            (JWT-authorized)
  • User syncs a trip  → POST /api/plaid/sync?trip_id=X  (JWT-authorized)
  • Plaid pushes       → POST /api/plaid/webhook          (anonymous, item_id lookup)
        │
        ▼
PlaidController → ITransactionSyncService (SyncForUserAsync / SyncForTripAsync)
        │
        ▼
TransactionSyncService
  1. Load the user's PlaidConnections
  2. Load planning/active trips, ordered by StartDate
  3. Compute window: min(lastSync - 2d, earliest trip start - 2d), cap 90d
  4. IPlaidService.GetTransactionsAsync(accessToken, window)
        │                (Going.Plaid → Plaid /transactions/get → PlaidTransaction DTOs)
        ▼
  5. For each transaction:
       credit (amount < 0)?        → skip
       pending?                    → skip (unstable ID)
       plaid_transaction_id known? → skip (dedup)
       TransactionMatchingService.FindMatchingTrip(date, trips)
         no match?                 → skip
       TransactionCategorizationService.Categorize(name, merchant)
       insert Expense(trip, category, decimal amount, Source="plaid")
  6. Stamp last_sync_at, SaveChanges (unique index backstops dedup)
        │
        ▼
Response: SyncResult { connectionsProcessed, transactionsFetched,
                       expensesUpserted, expensesSkipped, synced }
        │
        ▼
Next GET /api/trips/{id}/budget recomputes totals from the new rows —
nothing cached, the new expenses show up immediately.
```

## Testing

`tests/CampCost.Tests` (xUnit) covers the three Core domain services — the code where financial correctness lives:

- **BudgetCalculationServiceTests** — category-sum vs total-budget fallback, burn-rate math, the null-days-left case, day-count clamping.
- **TransactionMatchingServiceTests** — buffer windows, the today cap, overlapping trips resolving to the earliest start, no-date fallback, null when nothing matches.
- **TransactionCategorizationServiceTests** — merchant patterns per category and the `misc` fallback.

Because these services have no infrastructure dependencies, the tests need no mocks or database. The sync orchestrator itself is currently exercised through manual/sandbox testing; wiring an in-memory or test-container DB around `TransactionSyncService` is the natural next step.

## Deployment Shape

- **API on Railway** — the multi-stage `Dockerfile` restores from copied `.csproj` files (layer-cache friendly), publishes Release, and runs on the slim `aspnet:8.0` runtime image listening on `:8080`. All config via env vars (double-underscore keys, e.g. `Plaid__Secret`).
- **Frontend on Vercel** — static Vite build; `VITE_API_URL` points it at Railway.
- **Supabase** — Postgres, auth (RS256 JWTs the API validates), RLS policies from `client/supabase/migrations/001_init.sql`.
