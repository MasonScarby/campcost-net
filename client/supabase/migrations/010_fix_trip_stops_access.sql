-- ─────────────────────────────────────────────
-- 010: restore trip_stops access (Add Stop silently failed)
-- The live table matched 005's shape, but its RLS policy + grants never made it:
-- RLS was enabled with ZERO policies and authenticated had no CRUD grants, so every
-- select/insert was permission-denied (and the client swallowed the error).
-- ─────────────────────────────────────────────

-- CRUD for signed-in users only; anon gets nothing.
grant select, insert, update, delete on trip_stops to authenticated;

-- Ownership derived through the trip (no user_id column on stops) — same pattern
-- as budget_categories. FOR ALL's USING doubles as WITH CHECK for inserts.
drop policy if exists "Users own their stops" on trip_stops;
create policy "Users own their stops"
  on trip_stops for all using (
    auth.uid() = (select user_id from trips where id = trip_id)
  );
