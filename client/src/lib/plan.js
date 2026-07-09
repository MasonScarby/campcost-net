// Plan helpers — the SERVER is the source of truth (subscriptions table read by
// the API via authenticated JWT). localStorage is only a display cache so the UI
// doesn't flash while the fetch is in flight. All enforcement (7-day trip limit,
// 50-transactions-per-trip assignment cap) happens server-side and cannot be
// spoofed here — these constants exist only for display copy.
import { backendFetch } from './api'

export const FREE_TRIP_LIMIT = 1
export const FREE_MAX_TRIP_DAYS = 7
export const FREE_TXN_LIMIT = 50 // mirrors FreePlanLimits.MaxAssignedTransactionsPerTrip

const CACHE_KEY = 'campcost_plan'

/** Display cache only — never trusted for enforcement. */
export function getPlan() {
  return localStorage.getItem(CACHE_KEY) || 'free'
}

/** Updates the display cache (e.g. after fetchPlan resolves). */
export function setPlan(plan) {
  localStorage.setItem(CACHE_KEY, plan)
}

export function isPro() {
  return getPlan() === 'pro'
}

/**
 * Fetches the real plan from the API (GET /api/account/plan) and refreshes the
 * display cache. Falls back to the cached value if the backend is unreachable.
 */
export async function fetchPlan() {
  try {
    const res = await backendFetch('/api/account/plan')
    if (!res.ok) throw new Error(`Server error ${res.status}`)
    const { plan } = await res.json()
    if (plan === 'free' || plan === 'pro') {
      setPlan(plan)
      return plan
    }
  } catch {
    // Offline / backend down — use the cached value for display only
  }
  return getPlan()
}
