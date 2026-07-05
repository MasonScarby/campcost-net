// Dev-mode plan switcher — replace with Supabase subscriptions table read when Stripe is wired
export const FREE_TRIP_LIMIT = 1

export function getPlan() {
  return localStorage.getItem('campcost_plan') || 'free'
}

export function setPlan(plan) {
  localStorage.setItem('campcost_plan', plan)
}

export function isPro() {
  return getPlan() === 'pro'
}
