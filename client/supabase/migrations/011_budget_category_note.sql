-- Adds an optional "why" note to each budget category, captured when a user edits
-- the planned amount from the trip's budget breakdown. Latest note only (overwritten
-- on each edit). RLS is unchanged: budget_categories already restricts access via the
-- existing trip-join policy, so no new policy is needed.
alter table budget_categories add column if not exists note text;
