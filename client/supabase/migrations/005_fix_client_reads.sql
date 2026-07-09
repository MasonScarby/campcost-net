-- Fix the two client-facing breakages introduced around migration 004.
-- Run in the Supabase SQL editor AFTER 001–004.
--
-- 1) transactions: the browser needs deleted_at to mirror the backend's soft-delete
--    filter (Plaid `removed` deltas set deleted_at; without reading it the Dashboard
--    sums soft-deleted rows). Granting the single column keeps the 004 lockdown intact —
--    plaid_transaction_id / pending_transaction_id / updated_at stay browser-invisible.
--
-- 2) trip_stops: the live table's shape doesn't match what the Planned Stops UI
--    reads/writes (location/type/arrival_date/departure_date/cost_per_night/notes),
--    so every insert and order-by errors. The table is empty (verified July 2026),
--    so drop-and-recreate to the UI's exact shape beats guessing at renames.

-- ─────────────────────────────────────────────
-- 1) transactions: expose deleted_at (read-only, like the other granted columns)
-- ─────────────────────────────────────────────
grant select (deleted_at) on transactions to authenticated;

-- ─────────────────────────────────────────────
-- 2) trip_stops: recreate to match the client (safe — table has zero rows)
-- ─────────────────────────────────────────────
drop table if exists trip_stops;

create table trip_stops (
  id             uuid primary key default uuid_generate_v4(),
  trip_id        uuid references trips(id) on delete cascade not null,
  location       text not null,
  -- UI's stop types; 'other' is the catch-all
  type           text not null default 'campground'
                 check (type in ('campground','boondock','rv_park','city','other')),
  arrival_date   date,
  departure_date date,
  -- NULL means "no cost entered" so the estimated-lodging total skips it,
  -- rather than counting phantom $0 stops
  cost_per_night numeric(8,2),
  notes          text,
  created_at     timestamptz default now()
);

alter table trip_stops enable row level security;

-- Ownership is derived through the trip (no user_id column on stops) — same
-- pattern as budget_categories in 001.
create policy "Users own their stops"
  on trip_stops for all using (
    auth.uid() = (select user_id from trips where id = trip_id)
  );
