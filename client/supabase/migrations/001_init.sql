-- Enable RLS
-- Run this in Supabase SQL editor

create extension if not exists "uuid-ossp";

-- ─────────────────────────────────────────────
-- plaid_connections
-- ─────────────────────────────────────────────
create table plaid_connections (
  id uuid primary key default uuid_generate_v4(),
  user_id uuid references auth.users(id) on delete cascade not null,
  access_token text not null,  -- store encrypted; use Supabase Vault in prod
  item_id text not null,
  institution_name text,
  last_sync_at timestamptz,
  created_at timestamptz default now()
);
alter table plaid_connections enable row level security;
create policy "Users own their connections"
  on plaid_connections for all using (auth.uid() = user_id);

-- ─────────────────────────────────────────────
-- trips
-- ─────────────────────────────────────────────
create table trips (
  id uuid primary key default uuid_generate_v4(),
  user_id uuid references auth.users(id) on delete cascade not null,
  name text not null,
  destination text,
  start_date date,
  end_date date,
  total_budget numeric(10,2) default 0,
  total_miles numeric(10,1),
  status text not null default 'planning' check (status in ('planning','active','completed')),
  created_at timestamptz default now()
);
alter table trips enable row level security;
create policy "Users own their trips"
  on trips for all using (auth.uid() = user_id);

-- ─────────────────────────────────────────────
-- trip_stops
-- ─────────────────────────────────────────────
create table trip_stops (
  id uuid primary key default uuid_generate_v4(),
  trip_id uuid references trips(id) on delete cascade not null,
  location_name text,
  campground_type text check (campground_type in ('boondock','dispersed','partial','full_hookup','other')),
  cost_per_night numeric(8,2) default 0,
  nights integer default 1,
  sort_order integer default 0,
  created_at timestamptz default now()
);
alter table trip_stops enable row level security;
create policy "Users own their stops"
  on trip_stops for all using (
    auth.uid() = (select user_id from trips where id = trip_id)
  );

-- ─────────────────────────────────────────────
-- budget_categories
-- ─────────────────────────────────────────────
create type expense_category as enum (
  'fuel','campground','food_groceries','propane_utilities',
  'dump_station','activities','gear','repairs','misc'
);

create table budget_categories (
  id uuid primary key default uuid_generate_v4(),
  trip_id uuid references trips(id) on delete cascade not null,
  category expense_category not null,
  planned_amount numeric(10,2) default 0,
  unique(trip_id, category)
);
alter table budget_categories enable row level security;
create policy "Users own their budgets"
  on budget_categories for all using (
    auth.uid() = (select user_id from trips where id = trip_id)
  );

-- ─────────────────────────────────────────────
-- expenses
-- ─────────────────────────────────────────────
create table expenses (
  id uuid primary key default uuid_generate_v4(),
  trip_id uuid references trips(id) on delete cascade not null,
  user_id uuid references auth.users(id) on delete cascade not null,
  category expense_category not null default 'misc',
  amount numeric(10,2) not null,
  note text,
  expense_date date not null default current_date,
  source text not null default 'manual' check (source in ('plaid','manual')),
  plaid_transaction_id text unique,  -- prevent duplicate imports
  merchant_name text,
  reviewed boolean not null default false,
  created_at timestamptz default now()
);
alter table expenses enable row level security;
create policy "Users own their expenses"
  on expenses for all using (auth.uid() = user_id);

-- ─────────────────────────────────────────────
-- subscriptions
-- ─────────────────────────────────────────────
create table subscriptions (
  id uuid primary key default uuid_generate_v4(),
  user_id uuid references auth.users(id) on delete cascade not null unique,
  stripe_customer_id text,
  plan text not null default 'free' check (plan in ('free','pro')),
  status text not null default 'active',
  updated_at timestamptz default now()
);
alter table subscriptions enable row level security;
create policy "Users own their subscription"
  on subscriptions for all using (auth.uid() = user_id);

-- Auto-create free subscription on signup
create or replace function handle_new_user()
returns trigger language plpgsql security definer as $$
begin
  insert into subscriptions (user_id, plan, status)
  values (new.id, 'free', 'active');
  return new;
end;
$$;

create trigger on_auth_user_created
  after insert on auth.users
  for each row execute procedure handle_new_user();
