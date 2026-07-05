# CampCost

Budget tracking for RV and camping trips. Connects to your bank via Plaid, automatically categorizes transactions (fuel, campground, food, gear, repairs, etc.), and shows you real-time trip budget summaries with burn rate and days-left estimates.

## What It Does

- Connect your bank account via Plaid Link
- Sync transactions automatically or on-demand
- Auto-categorize spending by merchant name (Shell → fuel, KOA → campground, etc.)
- Track planned vs actual budget per trip and per category
- Calculate daily burn rate and estimated days left on the road
- Plan trip stops with locations and dates

## Tech Stack

| Layer | Technology |
|---|---|
| API | ASP.NET Core 8 |
| Frontend | React + Vite |
| Database | Supabase (PostgreSQL) |
| Auth | Supabase JWT (RS256) |
| Bank Integration | Plaid API |
| ORM | Entity Framework Core + Npgsql |

## Architecture

Clean Architecture — no reverse dependencies between layers.

```
CampCost.Core          → Entities, domain services, interfaces (no EF, no Plaid, no HTTP)
CampCost.Infrastructure → EF Core DbContext, PlaidService, TransactionSyncService
CampCost.Api           → Controllers, middleware (no business logic)
```

## Domain Model

```
Trip
 ├── TripStop[]       (planned route stops with arrival/departure dates)
 ├── Expense[]        (from Plaid sync or manual entry)
 └── BudgetCategory[] (planned spend per category)
```

**Trip statuses:** `planning` | `active` | `completed`
Sync only runs against `planning` and `active` trips.

## Expense Categories

Categories must match the `expense_category` PostgreSQL enum exactly:

| Value | Matches |
|---|---|
| `fuel` | Shell, BP, Chevron, Exxon, Pilot, Love's, Flying J, etc. |
| `campground` | KOA, HipCamp, ReserveAmerica, RV parks, state park campsites |
| `food_groceries` | Walmart, Kroger, Safeway, Aldi, Whole Foods, etc. |
| `gear` | REI, Cabela's, Bass Pro, Backcountry, etc. |
| `repairs` | AutoZone, O'Reilly, NAPA, mechanics, tire shops |
| `propane_utilities` | AmeriGas, Ferrellgas, Blue Rhino, dump stations |
| `activities` | National parks, entrance fees, recreation permits |
| `misc` | Fallback for anything unmatched |

## API Endpoints

All endpoints require a Supabase JWT Bearer token except the webhook.

### Trips

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/trips` | All planning/active trips for the current user, ordered by start date |
| `GET` | `/api/trips/{id}/budget` | Budget summary: planned, spent, money left, daily burn rate, days left |
| `GET` | `/api/trips/{id}/budget/by-category` | Planned vs actual broken down by expense category |

### Plaid

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/plaid/link-token` | Create a Plaid Link token for the frontend bank connection UI |
| `POST` | `/api/plaid/exchange-token` | Swap public token for access token, upsert on item_id |
| `POST` | `/api/plaid/sync` | Sync transactions. Optional `?trip_id=` to sync one specific trip |
| `POST` | `/api/plaid/webhook` | Plaid push webhook — processes TRANSACTIONS events |

### Health

| Method | Path | Description |
|---|---|---|
| `GET` | `/` | Redirects to Swagger UI |

## Transaction Sync Logic

**First connection:** 90 days back
**Incremental:** whichever is earlier — `lastSyncAt - 2 days` OR earliest active trip start date
**Hard cap:** 90 days maximum

**Dedup:** `plaid_transaction_id` unique index per user — same transaction can never be imported twice.

**Pending transactions:** skipped entirely. Plaid assigns a temporary ID while pending and a different permanent ID once settled, which causes duplicates. Only settled transactions are imported.

**Trip matching:** transactions are matched to trips by date window (`start_date - 2 days` to `end_date + 2 days`, capped at today). If no match → transaction is skipped, not randomly assigned. Overlapping trips resolve to the earliest start date.

**Skipped transactions** are recorded as: `reviewed=true, adjusted_amount=0, adjustment_note='skipped'`

## Budget Calculation

```
planned_budget = sum(budget_categories.planned_amount)  [if categories exist]
               = trip.total_budget                       [fallback]

money_left     = planned_budget - sum(expenses.amount)

daily_burn     = total_spent / days_elapsed_since_start  (minimum 1 day)

days_left      = money_left / daily_burn                 (null if no spending yet)
```

Budget is always recalculated from source data — never cached.

## Database Schema

### `trips`
| Column | Type | Notes |
|---|---|---|
| id | uuid | PK |
| user_id | uuid | FK to auth.users |
| name | text | |
| destination | text | nullable |
| start_date | date | nullable |
| end_date | date | nullable |
| total_budget | numeric | fallback if no budget_categories |
| status | text | planning \| active \| completed |
| created_at | timestamptz | |

### `expenses`
| Column | Type | Notes |
|---|---|---|
| id | uuid | PK |
| trip_id | uuid | FK to trips |
| user_id | uuid | FK to auth.users |
| plaid_transaction_id | text | UNIQUE per user, nullable |
| merchant_name | text | nullable |
| amount | numeric | always positive (credits skipped) |
| category | expense_category | DB enum |
| expense_date | date | |
| source | text | plaid \| manual |
| reviewed | boolean | |
| created_at | timestamptz | |

**Index:** `(user_id, plaid_transaction_id)` unique where `plaid_transaction_id IS NOT NULL`

### `plaid_connections`
| Column | Type | Notes |
|---|---|---|
| id | uuid | PK |
| user_id | uuid | FK to auth.users |
| access_token | text | Plaid permanent access token |
| item_id | text | Plaid item identifier |
| institution_name | text | |
| last_sync_at | timestamptz | nullable — null = never synced |
| created_at | timestamptz | |

### `trip_stops`
| Column | Type | Notes |
|---|---|---|
| id | uuid | PK |
| trip_id | uuid | FK to trips |
| location | text | |
| arrival_date | date | nullable |
| departure_date | date | nullable |
| notes | text | nullable |
| created_at | timestamptz | |

### `budget_categories`
| Column | Type | Notes |
|---|---|---|
| id | uuid | PK |
| trip_id | uuid | FK to trips |
| category | expense_category | DB enum |
| planned_amount | numeric | |
| created_at | timestamptz | |

### `subscriptions`
| Column | Type | Notes |
|---|---|---|
| id | uuid | PK |
| user_id | uuid | FK to auth.users |
| plan | text | |
| status | text | |
| created_at | timestamptz | |

## Local Setup

### Prerequisites
- .NET 8 SDK
- Node.js (for frontend)
- Supabase project with the schema above
- Plaid developer account (sandbox is free)

### Backend

```bash
cd CampCost.NET
dotnet restore

# Set secrets locally (never commit these)
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "your-supabase-connection-string"
dotnet user-secrets set "Supabase:Url" "https://your-project.supabase.co"
dotnet user-secrets set "Plaid:ClientId" "your-plaid-client-id"
dotnet user-secrets set "Plaid:Secret" "your-plaid-secret"
dotnet user-secrets set "Plaid:Environment" "sandbox"
dotnet user-secrets set "Plaid:WebhookUrl" "https://your-domain.com/api/plaid/webhook"

dotnet run --project src/CampCost.Api
# API runs at http://localhost:5033
# Swagger UI at http://localhost:5033/swagger
```

### Frontend

```bash
cd Projects/campcost
npm install
npm run dev
# Runs at http://localhost:5173
```

### Getting a JWT for Swagger Testing

In your browser console on the frontend:
```js
(await supabase.auth.getSession()).data.session.access_token
```
Paste into Swagger UI → Authorize.

## Environment Variables (Production)

| Variable | Description |
|---|---|
| `ConnectionStrings__DefaultConnection` | Supabase PostgreSQL connection string |
| `Supabase__Url` | Supabase project URL |
| `Plaid__ClientId` | Plaid client ID |
| `Plaid__Secret` | Plaid secret key |
| `Plaid__Environment` | `sandbox` \| `development` \| `production` |
| `Plaid__WebhookUrl` | Public URL for Plaid to POST transaction events |
| `Plaid__RedirectUri` | OAuth redirect URI (if using OAuth banks) |

## Known Issues Before Go-Live

See `CLAUDE.md` for the full list. Critical items:

1. **Webhook signature not validated** — anyone can POST to the webhook endpoint and trigger a sync
2. **Webhook SyncAllAsync fallback** — if item_id lookup fails, all users get synced
3. ~~**CORS hardcoded to localhost**~~ — fixed; set `Cors__AllowedOrigins__0=https://your-app.vercel.app` in production
4. **ExchangeToken missing UserId filter** — connection lookup doesn't scope to current user
5. **Null TransactionId generates random GUID** — breaks dedup

## Changelog

### 2026-07-05
- Fixed `trips[0]` silent fallback — unmatched transactions now return null and are explicitly skipped
- Added `OrderBy(StartDate)` to trips query — overlapping trip assignment is now deterministic
- Added `SyncForTripAsync` — trip-scoped sync with wrong-trip reassignment correction
- Added `Pending` flag to `PlaidTransaction` — pending transactions are now skipped to prevent duplicate imports
- Exposed `?trip_id` query param on `POST /api/plaid/sync`
- Upgraded CLAUDE.md to living architecture document
- Added Claude Code skills for dev workflow (systematic-debugger, financial-integrity, feature-planner, senior-code-reviewer, documentation-architect, security-auditor, architecture-decision-reviewer)

### Prior
- Upgraded from .NET 7 to .NET 8 LTS
- Fixed exchange-token JSON binding for snake_case `public_token` field
- Added budget calculation, trip stops, budget categories
- Aligned entities with Supabase schema
- Integrated Plaid via Going.Plaid SDK
