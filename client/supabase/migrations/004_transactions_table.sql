-- Decouple transaction ingestion from trip assignment.
-- Run in the Supabase SQL editor AFTER 001–003, and only once the matching
-- backend code (transactions-table refactor) is deployed — otherwise this table
-- sits empty while the app still writes Plaid data into `expenses`.
--
-- Model:
--   transactions = raw Plaid mirror, source of truth. Synced regardless of trips.
--                  trip_id is NULL until a trip claims it (auto by date range, or manual).
--   expenses     = manual entries only, going forward.
--   A trip's Plaid spend = SUM(transactions WHERE trip_id = trip AND deleted_at IS NULL).

-- ─────────────────────────────────────────────
-- transactions (source of truth for Plaid data)
-- ─────────────────────────────────────────────
create table if not exists transactions (
  id                    uuid primary key default uuid_generate_v4(),
  user_id               uuid references auth.users(id)        on delete cascade not null,
  plaid_connection_id   uuid references plaid_connections(id) on delete cascade not null,
  plaid_transaction_id  text not null,                       -- Plaid's id (settled id after reconciliation)
  pending_transaction_id text,                               -- links a settled txn back to its pending row
  trip_id               uuid references trips(id)            on delete set null,  -- NULL = unassigned
  amount                numeric(10,2) not null,
  transaction_date      date not null,
  merchant_name         text,
  name                  text,                                 -- Plaid raw description
  category              expense_category not null default 'misc',
  pending               boolean not null default false,
  reviewed              boolean not null default false,       -- user confirmed the trip assignment
  deleted_at            timestamptz,                          -- set when Plaid reports `removed`
  created_at            timestamptz default now(),
  updated_at            timestamptz default now(),
  unique (plaid_transaction_id)                               -- dedup, survives soft-delete
);

create index if not exists idx_transactions_user        on transactions(user_id);
create index if not exists idx_transactions_trip         on transactions(trip_id);
create index if not exists idx_transactions_connection   on transactions(plaid_connection_id);
create index if not exists idx_transactions_date         on transactions(transaction_date);

alter table transactions enable row level security;

-- Browser may READ its own transactions (for the review / assign screen).
-- All writes (insert from sync, assignment, soft-delete) go through the backend,
-- which connects as `postgres` and bypasses RLS. No write policy for browser roles.
create policy "Users read their transactions"
  on transactions for select using (auth.uid() = user_id);

-- Lock down to read-only from the browser, mirroring the 003 hardening pattern.
revoke all on table transactions from anon;
revoke all on table transactions from authenticated;
grant select (id, user_id, plaid_connection_id, trip_id, amount, transaction_date,
              merchant_name, name, category, pending, reviewed, created_at)
  on transactions to authenticated;

-- ─────────────────────────────────────────────
-- Migrate existing Plaid-sourced expenses into transactions, then remove them
-- from expenses so trip spend isn't double-counted. Manual expenses stay put.
-- ─────────────────────────────────────────────
insert into transactions (
  user_id, plaid_connection_id, plaid_transaction_id, trip_id, amount,
  transaction_date, merchant_name, name, category, pending, reviewed, deleted_at, created_at
)
select
  e.user_id,
  -- best-effort: attach to the user's connection; safe when a user has exactly one.
  (select pc.id from plaid_connections pc where pc.user_id = e.user_id order by pc.created_at limit 1),
  e.plaid_transaction_id,
  e.trip_id,
  e.amount,
  e.expense_date,
  e.merchant_name,
  e.note,
  e.category,
  false,
  e.reviewed,
  e.deleted_at,
  e.created_at
from expenses e
where e.source = 'plaid'
  and e.plaid_transaction_id is not null
  and not exists (
    select 1 from transactions t where t.plaid_transaction_id = e.plaid_transaction_id
  );

delete from expenses where source = 'plaid';

comment on table transactions is 'Raw Plaid transaction mirror. Source of truth; trip_id nullable until claimed.';
