-- The BudgetCategory entity (and EF mapping) include created_at, but 001 created
-- budget_categories without it — so every INSERT from the API (trip create with
-- budgets) failed with 42703 "column created_at does not exist".
-- Backfill default keeps existing rows valid.
alter table budget_categories
  add column if not exists created_at timestamptz default now();
