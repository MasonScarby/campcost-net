import { useEffect, useState } from 'react'
import { useParams, Link } from 'react-router-dom'
import { supabase } from '../lib/supabase'
import { useAuth } from '../context/AuthContext'
import { PieChart, Pie, Cell, Tooltip, ResponsiveContainer, BarChart, Bar, XAxis, YAxis, CartesianGrid } from 'recharts'

import { backendFetch } from '../lib/api'

const CATEGORY_LABELS = {
  fuel: 'Fuel', campground: 'Campground', food_groceries: 'Food & Groceries',
  propane_utilities: 'Propane & Utilities', dump_station: 'Dump Station',
  activities: 'Activities', gear: 'Gear', repairs: 'Repairs', misc: 'Misc',
}

const card = { background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 20, marginBottom: 12 }

// Explicit column list for browser reads of `transactions`. Migration 004 revoked table-wide
// SELECT and re-granted only safe columns, so select('*') = permission denied (PostgREST
// expands * to every column). Keep this in sync with the grant in 005_fix_client_reads.sql.
const TXN_COLUMNS = 'id, trip_id, amount, transaction_date, merchant_name, name, category, pending, reviewed, deleted_at, adjusted_amount, adjustment_note'

export default function TripDetail() {
  const { id } = useParams()
  const { user } = useAuth()
  const [trip, setTrip] = useState(null)
  const [budgets, setBudgets] = useState([])
  const [expenses, setExpenses] = useState([]) // manual entries only
  const [transactions, setTransactions] = useState([]) // Plaid mirror assigned to this trip
  const [txnBusyId, setTxnBusyId] = useState(null)
  const [expandedTxnId, setExpandedTxnId] = useState(null)
  const [loading, setLoading] = useState(true)
  const [showAddCash, setShowAddCash] = useState(false)
  const [cashAmount, setCashAmount] = useState('')
  const [cashCategory, setCashCategory] = useState('misc')
  const [cashNote, setCashNote] = useState('')
  const [addingCash, setAddingCash] = useState(false)
  const [syncStatus, setSyncStatus] = useState('idle') // idle | syncing | done
  const [capWarning, setCapWarning] = useState(null) // free-plan 50-txn assignment cap hit
  const [editingId, setEditingId] = useState(null)
  const [editShare, setEditShare] = useState('')
  const [editNote, setEditNote] = useState('')
  const [savingEdit, setSavingEdit] = useState(false)
  const [editError, setEditError] = useState('')

  // Trip date editing
  const [editingDates, setEditingDates] = useState(false)
  const [dateForm, setDateForm] = useState({ start_date: '', end_date: '' })
  const [savingDates, setSavingDates] = useState(false)
  const [dateError, setDateError] = useState('')

  // Reset transactions
  const [showResetConfirm, setShowResetConfirm] = useState(false)
  const [resetting, setResetting] = useState(false)

  // Filters
  const [filterSearch, setFilterSearch] = useState('')
  const [filterCategory, setFilterCategory] = useState('')
  const [filterSort, setFilterSort] = useState('')

  // In-window transactions living elsewhere (other trips or unassigned). Reviewed rows
  // are never auto-moved, so without this list a transaction reviewed onto an overlapping
  // trip is invisible from this one forever — manual "Move here" is the only escape hatch.
  const [windowTxns, setWindowTxns] = useState([])
  const [tripNames, setTripNames] = useState({})

  // Campground stops
  const [stops, setStops] = useState([])
  const [showAddStop, setShowAddStop] = useState(false)
  const [stopForm, setStopForm] = useState({ location: '', type: 'campground', arrival_date: '', departure_date: '', cost_per_night: '', notes: '' })
  const [savingStop, setSavingStop] = useState(false)

  // In-window transactions NOT on this trip (other trips or unassigned, incl. reviewed).
  // The .or() is needed because trip_id.neq alone excludes NULLs in SQL.
  const fetchWindowTxns = async (start, end) => {
    const [{ data: others }, { data: allTrips }] = await Promise.all([
      supabase.from('transactions').select(TXN_COLUMNS)
        .gte('transaction_date', start).lte('transaction_date', end)
        .is('deleted_at', null)
        .or(`trip_id.neq.${id},trip_id.is.null`)
        .order('transaction_date', { ascending: false }),
      supabase.from('trips').select('id, name').eq('user_id', user.id),
    ])
    setWindowTxns(others ?? [])
    setTripNames(Object.fromEntries((allTrips ?? []).map(t => [t.id, t.name])))
  }

  useEffect(() => {
    const fetchAll = async () => {
      const [{ data: t }, { data: b }, { data: e }, { data: x }, { data: s }] = await Promise.all([
        supabase.from('trips').select('*').eq('id', id).eq('user_id', user.id).single(),
        supabase.from('budget_categories').select('*').eq('trip_id', id),
        // expenses = manual entries only, going forward
        supabase.from('expenses').select('*').eq('trip_id', id).order('expense_date', { ascending: false }),
        // transactions = Plaid mirror (source of truth) assigned to this trip.
        // Browser has read-only RLS access; all writes go through the API.
        // NEVER select('*') here: migration 004 grants column-level SELECT, and PostgREST
        // expands * to ALL columns (incl. ungranted plaid_transaction_id etc.), which fails
        // the whole query with permission denied — the trip then renders $0 / empty.
        // deleted_at filter mirrors the backend's soft-delete query filter.
        supabase.from('transactions').select(TXN_COLUMNS).eq('trip_id', id).is('deleted_at', null).order('transaction_date', { ascending: false }),
        supabase.from('trip_stops').select('*').eq('trip_id', id).order('arrival_date', { ascending: true }),
      ])
      setTrip(t)
      setBudgets(b ?? [])
      setExpenses(e ?? [])
      setTransactions(x ?? [])
      setStops(s ?? [])
      if (t?.start_date && t?.end_date) {
        await fetchWindowTxns(t.start_date, t.end_date)
      }
      setLoading(false)
    }
    fetchAll()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id, user.id])

  // Pull an elsewhere-living transaction onto THIS trip. Manual assign may move
  // reviewed rows (only AUTO-assign must respect them); marks reviewed on the way.
  const claimTransaction = async (txn) => {
    setTxnBusyId(txn.id)
    try {
      const res = await backendFetch(`/api/transactions/${txn.id}/assign`, {
        method: 'PATCH',
        body: JSON.stringify({ trip_id: id }),
      })
      if (res.ok) {
        setWindowTxns(prev => prev.filter(t => t.id !== txn.id))
        setTransactions(prev => [{ ...txn, trip_id: id, reviewed: true }, ...prev])
      } else if (res.status === 402) {
        const data = await res.json().catch(() => ({}))
        setCapWarning(data.error || 'Free plan transaction limit reached — upgrade for unlimited.')
      }
    } finally {
      setTxnBusyId(null)
    }
  }

  const fetchExpenses = async () => {
    const { data: e } = await supabase
      .from('expenses').select('*').eq('trip_id', id).order('expense_date', { ascending: false })
    setExpenses(e ?? [])
  }

  // Re-pull the Plaid transactions currently assigned to this trip (source of truth).
  // Explicit columns + deleted_at filter — see TXN_COLUMNS comment for why * breaks.
  const fetchTransactions = async () => {
    const { data: x } = await supabase
      .from('transactions').select(TXN_COLUMNS).eq('trip_id', id).is('deleted_at', null).order('transaction_date', { ascending: false })
    setTransactions(x ?? [])
  }

  const handleSync = async () => {
    setSyncStatus('syncing')
    try {
      const res = await backendFetch(`/api/plaid/sync?trip_id=${id}`, { method: 'POST' })
      const result = await res.json()
      console.log('[CampCost sync] fetched:', result.transactionsFetched, '| upserted:', result.expensesUpserted, '| removed:', result.expensesRemoved)
      await Promise.all([fetchExpenses(), fetchTransactions()])
      setSyncStatus('done')
      setTimeout(() => setSyncStatus('idle'), 3000)
    } catch {
      setSyncStatus('idle')
    }
  }

  // Plaid transactions are an immutable mirror — the browser can't write them (RLS).
  // Re-assignment/review goes through the API. Passing tripId=null removes the
  // transaction from this trip (unassign); passing this trip's id confirms it (reviewed=true).
  const updateTransactionAssignment = async (txnId, tripId) => {
    setTxnBusyId(txnId)
    try {
      const res = await backendFetch(`/api/transactions/${txnId}/assign`, {
        method: 'PATCH',
        body: JSON.stringify({ trip_id: tripId }),
      })
      if (res.ok) {
        if (tripId === null) {
          // No longer on this trip — surface it in the "other transactions" list so it
          // stays reachable (it's reviewed now, so auto-assign will never reclaim it).
          const removed = transactions.find(t => t.id === txnId)
          setTransactions(prev => prev.filter(t => t.id !== txnId))
          if (removed) setWindowTxns(prev => [{ ...removed, trip_id: null, reviewed: true }, ...prev])
        } else {
          setTransactions(prev => prev.map(t => t.id === txnId ? { ...t, reviewed: true } : t))
        }
        setExpandedTxnId(null)
      } else if (res.status === 402) {
        // Free-plan trip already holds 50 assigned transactions — surface the upgrade nudge
        const data = await res.json().catch(() => ({}))
        setCapWarning(data.error || 'Free plan transaction limit reached — upgrade for unlimited.')
      }
    } finally {
      setTxnBusyId(null)
    }
  }

  // Re-categorize a bank transaction after it's been reviewed. The assign endpoint takes a
  // category override (validated server-side against the expense_category enum) and writes it
  // alongside trip_id — same immutable-mirror-safe path as confirm/remove. Keeps it on this trip.
  const changeTransactionCategory = async (txnId, category) => {
    setTxnBusyId(txnId)
    try {
      const res = await backendFetch(`/api/transactions/${txnId}/assign`, {
        method: 'PATCH',
        body: JSON.stringify({ trip_id: id, category }),
      })
      if (res.ok) {
        setTransactions(prev => prev.map(t => t.id === txnId ? { ...t, category, reviewed: true } : t))
      } else {
        const data = await res.json().catch(() => ({}))
        setEditError(data.error || `Couldn't change category (${res.status})`)
      }
    } finally {
      setTxnBusyId(null)
    }
  }

  const effectiveAmount = (e) =>
    e.adjusted_amount != null ? Number(e.adjusted_amount) : Number(e.amount)

  const openEdit = (e) => {
    setEditingId(e.id)
    setEditShare(effectiveAmount(e).toFixed(2))
    setEditNote(e.adjustment_note || '')
    setEditError('')
  }

  const saveAdjustment = async (expense) => {
    setSavingEdit(true)
    setEditError('')
    const share = parseFloat(editShare)
    if (isNaN(share) || share < 0) {
      setEditError('Enter a valid amount.')
      setSavingEdit(false)
      return
    }
    const isAdjusted = share !== Number(expense.amount)
    const { error } = await supabase
      .from('expenses')
      .update({
        adjusted_amount: isAdjusted ? share : null,
        adjustment_note: editNote.trim() || null,
      })
      .eq('id', expense.id)
    if (error) {
      console.error('adjustment error:', error)
      setEditError(error.message.includes('column') ? 'Run the SQL migration first — see instructions above.' : error.message)
      setSavingEdit(false)
      return
    }
    setExpenses(prev => prev.map(e =>
      e.id === expense.id
        ? { ...e, adjusted_amount: isAdjusted ? share : null, adjustment_note: editNote.trim() || null }
        : e
    ))
    setSavingEdit(false)
    setEditingId(null)
  }

  const clearAdjustment = async (expense) => {
    await supabase.from('expenses').update({ adjusted_amount: null, adjustment_note: null }).eq('id', expense.id)
    setExpenses(prev => prev.map(e =>
      e.id === expense.id ? { ...e, adjusted_amount: null, adjustment_note: null } : e
    ))
    setEditingId(null)
  }

  // Split/adjust for BANK transactions — the raw Plaid amount is immutable, so this
  // writes the adjusted_amount overlay via the API (PATCH /adjust), never Supabase.
  const savePlaidAdjustment = async (txn) => {
    setSavingEdit(true)
    setEditError('')
    const share = parseFloat(editShare)
    if (isNaN(share) || share < 0) {
      setEditError('Enter a valid amount.')
      setSavingEdit(false)
      return
    }
    const isAdjusted = share !== Number(txn.amount)
    const body = {
      adjusted_amount: isAdjusted ? share : null,
      adjustment_note: isAdjusted ? (editNote.trim() || null) : null,
    }
    try {
      const res = await backendFetch(`/api/transactions/${txn.id}/adjust`, {
        method: 'PATCH',
        body: JSON.stringify(body),
      })
      if (!res.ok) {
        const data = await res.json().catch(() => ({}))
        setEditError(data.error || `Server error ${res.status}`)
        setSavingEdit(false)
        return
      }
      setTransactions(prev => prev.map(t =>
        t.id === txn.id ? { ...t, adjusted_amount: body.adjusted_amount, adjustment_note: body.adjustment_note } : t
      ))
      setExpandedTxnId(null)
    } catch (err) {
      setEditError(err.message === 'Failed to fetch' ? 'Backend is not running.' : err.message)
    }
    setSavingEdit(false)
  }

  const clearPlaidAdjustment = async (txn) => {
    const res = await backendFetch(`/api/transactions/${txn.id}/adjust`, {
      method: 'PATCH',
      body: JSON.stringify({ adjusted_amount: null, adjustment_note: null }),
    })
    if (res.ok) {
      setTransactions(prev => prev.map(t =>
        t.id === txn.id ? { ...t, adjusted_amount: null, adjustment_note: null } : t
      ))
      setExpandedTxnId(null)
    }
  }

  const addStop = async () => {
    if (!stopForm.location.trim()) return
    setSavingStop(true)
    // No user_id column on trip_stops — ownership is derived through the trip by RLS
    // ("Users own their stops" joins trips.user_id). Sending user_id fails the insert.
    const { data, error } = await supabase.from('trip_stops').insert({
      trip_id: id,
      location: stopForm.location.trim(),
      type: stopForm.type,
      arrival_date: stopForm.arrival_date || null,
      departure_date: stopForm.departure_date || null,
      cost_per_night: stopForm.cost_per_night ? Number(stopForm.cost_per_night) : null,
      notes: stopForm.notes.trim() || null,
    }).select().single()
    if (!error && data) {
      setStops(prev => [...prev, data].sort((a, b) => (a.arrival_date || '').localeCompare(b.arrival_date || '')))
      setStopForm({ location: '', type: 'campground', arrival_date: '', departure_date: '', cost_per_night: '', notes: '' })
      setShowAddStop(false)
    }
    setSavingStop(false)
  }

  const deleteStop = async (stopId) => {
    await supabase.from('trip_stops').delete().eq('id', stopId)
    setStops(prev => prev.filter(s => s.id !== stopId))
  }

  const saveDates = async () => {
    setSavingDates(true)
    setDateError('')
    // Goes through the API (not direct Supabase) so the free-plan 7-day trip limit
    // is enforced server-side — a 402 means "upgrade to Pro". The API also re-runs
    // auto-assign for the new window and reports the 50-txn cap via `warning`.
    let error = null
    try {
      const res = await backendFetch(`/api/trips/${id}`, {
        method: 'PUT',
        body: JSON.stringify({
          start_date: dateForm.start_date || null,
          end_date: dateForm.end_date || null,
        }),
      })
      const data = await res.json()
      if (!res.ok) {
        setDateError(data.error || `Server error ${res.status}`)
        error = data
      } else {
        setCapWarning(data.warning ?? null)
      }
    } catch (err) {
      setDateError(err.message === 'Failed to fetch' ? 'Backend is not running.' : err.message)
      error = err
    }
    if (!error) {
      setTrip(prev => ({ ...prev, start_date: dateForm.start_date || null, end_date: dateForm.end_date || null }))
      setEditingDates(false)
      // Auto-sync with this trip's date range so expanded dates pull new transactions
      try {
        setSyncStatus('syncing')
        await backendFetch(`/api/plaid/sync?trip_id=${id}`, { method: 'POST' })
        await Promise.all([fetchExpenses(), fetchTransactions()])
        setSyncStatus('done')
        setTimeout(() => setSyncStatus('idle'), 3000)
      } catch {
        setSyncStatus('idle')
      }
    }
    setSavingDates(false)
  }

  const deleteExpense = async (expenseId) => {
    await supabase.from('expenses').delete().eq('id', expenseId)
    setExpenses(prev => prev.filter(e => e.id !== expenseId))
  }

  // Re-pull bank transactions. The old flow hard-deleted Plaid rows from `expenses` —
  // but `transactions` is now the immutable source of truth and must NEVER be destroyed.
  // Instead: reset the Plaid sync cursor (backend, RLS-safe) so the next sync re-pulls the
  // full history, then run a trip-scoped sync that re-claims this trip's window. Dedup on
  // plaid_transaction_id means nothing is duplicated and no stored transaction is lost.
  const resetTransactions = async () => {
    setResetting(true)
    try {
      setSyncStatus('syncing')
      await backendFetch('/api/plaid/reset-cursor', { method: 'POST' })
      await backendFetch(`/api/plaid/sync?trip_id=${id}`, { method: 'POST' })
      await Promise.all([fetchExpenses(), fetchTransactions()])
      setSyncStatus('done')
      setTimeout(() => setSyncStatus('idle'), 3000)
    } catch {
      setSyncStatus('idle')
    }
    setShowResetConfirm(false)
    setResetting(false)
  }

  const addCashExpense = async () => {
    if (!cashAmount || Number(cashAmount) <= 0) return
    setAddingCash(true)
    const { data } = await supabase.from('expenses').insert({
      trip_id: id, user_id: user.id, category: cashCategory,
      amount: Number(cashAmount), note: cashNote || null,
      expense_date: new Date().toISOString().split('T')[0],
      source: 'manual', reviewed: true,
    }).select().single()
    if (data) setExpenses(prev => [data, ...prev])
    setCashAmount(''); setCashCategory('misc'); setCashNote('')
    setShowAddCash(false); setAddingCash(false)
  }

  if (loading) return (
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', minHeight: '60vh' }}>
      <div style={{ width: 32, height: 32, border: '4px solid #2d5a27', borderTopColor: 'transparent', borderRadius: '50%', animation: 'spin 0.8s linear infinite' }} />
      <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
    </div>
  )

  if (!trip) return <div style={{ padding: 32, textAlign: 'center', color: '#8b5e3c' }}>Trip not found.</div>

  // Spend mirrors the Dashboard's computation exactly:
  //   Plaid transactions assigned to this trip (source of truth) — ALL of them count,
  //   reviewed or not, since they're already scoped to the trip by trip_id.
  //   Manual expenses count only once reviewed (honoring adjusted amounts, skipping $0 "skipped").
  const reviewedManual = expenses.filter(e => e.reviewed !== false && e.adjustment_note !== 'skipped')
  // effectiveAmount honors splits on BOTH sources — transactions share the same
  // adjusted_amount/amount field names as expenses (007 migration).
  const plaidSpent = transactions.reduce((s, t) => s + effectiveAmount(t), 0)
  const manualSpent = reviewedManual.reduce((s, e) => s + effectiveAmount(e), 0)
  const totalSpent = plaidSpent + manualSpent

  // Unreviewed Plaid transactions on this trip: auto-added and already counted, but the
  // user hasn't confirmed they belong. Surfaced for review (confirm or remove).
  const unreviewedTxns = transactions.filter(t => !t.reviewed)
  const unreviewedTotal = unreviewedTxns.reduce((s, t) => s + effectiveAmount(t), 0)

  const totalBudget = Number(trip.total_budget) || 0
  const pct = totalBudget > 0 ? Math.min((totalSpent / totalBudget) * 100, 100) : 0

  // Combined spend lines (Plaid + reviewed manual) drive the category + daily breakdowns.
  const spendLines = [
    ...transactions.map(t => ({ category: t.category, amount: effectiveAmount(t), date: t.transaction_date })),
    ...reviewedManual.map(e => ({ category: e.category, amount: effectiveAmount(e), date: e.expense_date })),
  ]
  const spentByCategory = spendLines.reduce((acc, l) => {
    acc[l.category] = (acc[l.category] || 0) + l.amount; return acc
  }, {})
  const nights = trip.start_date && trip.end_date
    ? Math.max(1, Math.round((new Date(trip.end_date) - new Date(trip.start_date)) / 86400000)) : null
  const costPerNight = nights ? (totalSpent / nights).toFixed(2) : null
  const costPerMile = trip.total_miles ? (totalSpent / trip.total_miles).toFixed(2) : null
  const barColor = pct >= 90 ? '#d4622a' : pct >= 70 ? '#e07440' : '#2d5a27'

  const unreviewedCount = unreviewedTxns.length

  const today = new Date()
  today.setHours(0, 0, 0, 0)
  const tripStart = trip.start_date ? new Date(trip.start_date) : null
  const tripEnd = trip.end_date ? new Date(trip.end_date) : null
  const tripIsActive = tripStart && tripStart <= today && (!tripEnd || tripEnd >= today)

  // Day progress
  const totalDays = tripStart && tripEnd
    ? Math.round((tripEnd - tripStart) / 86400000) + 1 : null
  const dayNumber = tripStart
    ? Math.min(Math.round((today - tripStart) / 86400000) + 1, totalDays ?? Infinity) : null
  const daysLeft = tripEnd ? Math.max(0, Math.round((tripEnd - today) / 86400000)) : null
  const dayPct = totalDays && dayNumber ? Math.min((dayNumber / totalDays) * 100, 100) : 0
  const tripNotStarted = tripStart && tripStart > today
  const tripOver = tripEnd && tripEnd < today

  const CATEGORY_COLORS = {
    fuel: '#e07440', campground: '#2d5a27', food_groceries: '#8b5e3c',
    propane_utilities: '#4a8c7a', dump_station: '#c4956a', activities: '#6b9c5e',
    gear: '#9b7bb8', repairs: '#d4622a', misc: '#a89070',
  }

  // Unified list: Plaid transactions + manual expenses in one feed. `source` drives the
  // badge and which actions a row offers (Plaid → remove/confirm via API; manual → adjust).
  const lineItems = [
    ...transactions.map(t => ({
      key: `t-${t.id}`, id: t.id, source: 'plaid',
      date: t.transaction_date,
      title: t.merchant_name || t.name || CATEGORY_LABELS[t.category] || 'Transaction',
      searchText: `${t.merchant_name || ''} ${t.name || ''}`,
      amount: effectiveAmount(t), original: Number(t.amount),
      category: t.category, reviewed: t.reviewed, pending: t.pending,
      adjusted: t.adjusted_amount != null, adjustmentNote: t.adjustment_note, raw: t,
    })),
    ...expenses.filter(e => e.adjustment_note !== 'skipped').map(e => ({
      key: `e-${e.id}`, id: e.id, source: 'manual',
      date: e.expense_date,
      title: e.note || CATEGORY_LABELS[e.category] || 'Expense',
      searchText: `${e.merchant_name || ''} ${e.note || ''}`,
      amount: effectiveAmount(e), original: Number(e.amount),
      category: e.category, reviewed: e.reviewed !== false, pending: false,
      adjusted: e.adjusted_amount != null, adjustmentNote: e.adjustment_note, raw: e,
    })),
  ]

  const filteredItems = lineItems
    .filter(it => !filterCategory || it.category === filterCategory)
    .filter(it => {
      if (!filterSearch) return true
      const term = filterSearch.toLowerCase()
      return it.searchText.toLowerCase().includes(term) || it.title.toLowerCase().includes(term)
    })
    .sort((a, b) => {
      if (filterSort === 'asc') return a.amount - b.amount
      if (filterSort === 'desc') return b.amount - a.amount
      return (b.date || '').localeCompare(a.date || '')
    })

  const donutData = Object.entries(spentByCategory)
    .filter(([, v]) => v > 0)
    .map(([cat, value]) => ({ name: CATEGORY_LABELS[cat] || cat, value: Number(value.toFixed(2)), cat }))

  const dailyData = Object.entries(
    spendLines.reduce((acc, l) => {
      if (l.date) acc[l.date] = (acc[l.date] || 0) + l.amount
      return acc
    }, {})
  )
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([date, amount]) => ({ date: date.slice(5), amount: Number(amount.toFixed(2)) }))

  return (
    <div style={{ maxWidth: 700, margin: '0 auto', padding: '32px 16px' }}>

      {/* Header */}
      <div style={{ marginBottom: 24 }}>
        <Link to="/dashboard" style={{ fontSize: 14, color: '#8b5e3c', textDecoration: 'none', display: 'block', marginBottom: 10 }}>
          ← All trips
        </Link>
        <div>
          <h1 style={{ fontSize: 26, fontWeight: 700, color: '#4a3728', margin: 0 }}>{trip.name}</h1>
          {trip.destination && <p style={{ fontSize: 15, color: '#8b5e3c', marginTop: 4 }}>{trip.destination}</p>}
        </div>
      </div>

      {/* Pending review banner */}
      {unreviewedCount > 0 && (
        <div style={{ background: '#fdf6e8', border: '1px solid #e8d5b0', borderRadius: 14, padding: '14px 18px', marginBottom: 16, display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12 }}>
          <div>
            <p style={{ fontSize: 15, fontWeight: 700, color: '#4a3728', margin: 0 }}>
              ${unreviewedTotal.toFixed(2)} to review
            </p>
            <p style={{ fontSize: 12, color: '#8b5e3c', margin: '2px 0 0' }}>
              {unreviewedCount} bank transaction{unreviewedCount !== 1 ? 's' : ''} auto-added — confirm or remove
            </p>
          </div>
          <Link to={`/trips/${id}/review`} style={{
            background: '#2d5a27', color: 'white', padding: '8px 16px',
            borderRadius: 10, fontSize: 13, fontWeight: 600, textDecoration: 'none', whiteSpace: 'nowrap'
          }}>
            Review
          </Link>
        </div>
      )}

      {/* Check for new transactions — shown when trip is active */}
      {tripIsActive && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 16 }}>
          <button
            onClick={handleSync}
            disabled={syncStatus === 'syncing'}
            style={{
              display: 'flex', alignItems: 'center', gap: 6,
              background: 'white', border: '1px solid #e8d5b0', borderRadius: 10,
              padding: '7px 14px', fontSize: 13, fontWeight: 600, color: '#4a3728',
              cursor: syncStatus === 'syncing' ? 'not-allowed' : 'pointer',
              opacity: syncStatus === 'syncing' ? 0.6 : 1,
            }}
          >
            <span style={{ fontSize: 14 }}>↻</span>
            {syncStatus === 'syncing' ? 'Checking...' : 'Check for new transactions'}
          </button>
          {syncStatus === 'done' && !capWarning && (
            <span style={{ fontSize: 12, color: '#2d5a27', fontWeight: 600 }}>Up to date</span>
          )}
        </div>
      )}

      {capWarning && (
        <p style={{
          fontSize: 12, color: '#8a5a00', background: '#fff7e0',
          border: '1px solid #f0dfae', borderRadius: 8, padding: '10px 14px', marginBottom: 16
        }}>
          ⚠ {capWarning}
        </p>
      )}

      {/* Summary */}
      <div style={card}>
        <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 15, marginBottom: 10 }}>
          <span style={{ color: '#8b5e3c' }}>Total spent</span>
          <span style={{ fontWeight: 700, color: '#4a3728' }}>${totalSpent.toFixed(2)} / ${totalBudget.toFixed(0)}</span>
        </div>
        <div style={{ height: 12, background: '#f5e6c8', borderRadius: 99, overflow: 'hidden', marginBottom: 12 }}>
          <div style={{ height: '100%', width: `${pct}%`, background: barColor, borderRadius: 99 }} />
        </div>
        <div style={{ display: 'flex', gap: 20, fontSize: 13, color: '#8b5e3c' }}>
          {costPerNight && <span>~${costPerNight}/night</span>}
          {costPerMile && <span>~${costPerMile}/mile</span>}
          {nights && <span>{nights} nights</span>}
        </div>

        {/* Day progress tracker */}
        {tripStart && (
          <div style={{ marginTop: 16, paddingTop: 16, borderTop: '1px solid #f5e6c8' }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', marginBottom: 8 }}>

              <span style={{ fontSize: 13, fontWeight: 700, color: '#4a3728' }}>
                {tripNotStarted
                  ? `Starts in ${Math.round((tripStart - today) / 86400000)} day${Math.round((tripStart - today) / 86400000) !== 1 ? 's' : ''}`
                  : tripOver
                  ? 'Trip completed'
                  : totalDays
                  ? `Day ${dayNumber} of ${totalDays}`
                  : `Day ${dayNumber}`}
              </span>
              <span style={{ fontSize: 12, color: '#8b5e3c' }}>
                {tripNotStarted && trip.start_date}
                {!tripNotStarted && !tripOver && daysLeft !== null && `${daysLeft} day${daysLeft !== 1 ? 's' : ''} left`}
                {!tripNotStarted && !tripOver && daysLeft === null && 'No end date set'}
                {tripOver && trip.end_date}
              </span>
            </div>

            {totalDays && (
              <div style={{ position: 'relative', height: 8, background: '#f5e6c8', borderRadius: 99, overflow: 'visible' }}>
                <div style={{
                  height: '100%', borderRadius: 99,
                  width: `${dayPct}%`,
                  background: tripOver ? '#c4b5a5' : tripNotStarted ? '#f5e6c8' : '#2d5a27',
                  transition: 'width 0.4s',
                }} />
                {/* Today marker */}
                {!tripNotStarted && !tripOver && (
                  <div style={{
                    position: 'absolute', top: '50%', left: `${dayPct}%`,
                    transform: 'translate(-50%, -50%)',
                    width: 14, height: 14, borderRadius: '50%',
                    background: '#2d5a27', border: '2px solid white',
                    boxShadow: '0 1px 4px rgba(0,0,0,0.2)',
                  }} />
                )}
              </div>
            )}

            {totalDays && (
              <>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', fontSize: 11, color: '#b0a090', marginTop: 6 }}>
                  <span>{trip.start_date}</span>
                  <span>{trip.end_date}</span>
                </div>
                <div style={{ display: 'flex', justifyContent: 'center', marginTop: 10 }}>
                  <button
                    onClick={() => { setDateForm({ start_date: trip.start_date || '', end_date: trip.end_date || '' }); setEditingDates(true) }}
                    style={{ background: '#f5e6c8', border: '1px solid #e8d5b0', borderRadius: 8, fontSize: 13, fontWeight: 600, color: '#8b5e3c', cursor: 'pointer', padding: '6px 18px' }}
                  >
                    Edit dates
                  </button>
                </div>
              </>
            )}

            {editingDates && (
              <div style={{ background: '#fdf6e8', border: '1px solid #e8d5b0', borderRadius: 12, padding: 14, marginTop: 12, display: 'flex', flexDirection: 'column', gap: 10 }}>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8 }}>
                  <div>
                    <label style={{ fontSize: 11, color: '#8b5e3c', fontWeight: 600, display: 'block', marginBottom: 4 }}>Start date</label>
                    <input type="date" value={dateForm.start_date} onChange={e => setDateForm(p => ({ ...p, start_date: e.target.value }))}
                      style={{ width: '100%', border: '1px solid #e8d5b0', borderRadius: 10, padding: '8px 10px', fontSize: 13, outline: 'none', boxSizing: 'border-box' }} />
                  </div>
                  <div>
                    <label style={{ fontSize: 11, color: '#8b5e3c', fontWeight: 600, display: 'block', marginBottom: 4 }}>End date</label>
                    <input type="date" value={dateForm.end_date} onChange={e => setDateForm(p => ({ ...p, end_date: e.target.value }))}
                      style={{ width: '100%', border: '1px solid #e8d5b0', borderRadius: 10, padding: '8px 10px', fontSize: 13, outline: 'none', boxSizing: 'border-box' }} />
                  </div>
                </div>
                {dateError && (
                  <p style={{ color: '#d4622a', fontSize: 12, margin: 0, padding: '8px 10px', background: '#fff5f0', borderRadius: 8 }}>
                    {dateError}
                  </p>
                )}
                <div style={{ display: 'flex', gap: 8 }}>
                  <button onClick={saveDates} disabled={savingDates}
                    style={{ flex: 1, background: '#2d5a27', color: 'white', border: 'none', borderRadius: 10, padding: '8px', fontSize: 13, fontWeight: 600, cursor: 'pointer', opacity: savingDates ? 0.6 : 1 }}>
                    {savingDates ? 'Saving...' : 'Save'}
                  </button>
                  <button onClick={() => setEditingDates(false)}
                    style={{ flex: 1, background: '#f5e6c8', color: '#8b5e3c', border: 'none', borderRadius: 10, padding: '8px', fontSize: 13, fontWeight: 600, cursor: 'pointer' }}>
                    Cancel
                  </button>
                </div>
              </div>
            )}
          </div>
        )}
      </div>

      {/* Campground Stops */}
      <div style={card}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: stops.length > 0 || showAddStop ? 16 : 0 }}>
          <div>
            <h2 style={{ fontSize: 17, fontWeight: 600, color: '#4a3728', margin: 0 }}>Planned Stops</h2>
            {stops.length > 0 && (() => {
              const totalStopCost = stops.reduce((sum, s) => {
                if (!s.cost_per_night || !s.arrival_date || !s.departure_date) return sum
                const n = Math.max(0, Math.round((new Date(s.departure_date) - new Date(s.arrival_date)) / 86400000))
                return sum + n * Number(s.cost_per_night)
              }, 0)
              return totalStopCost > 0
                ? <p style={{ fontSize: 12, color: '#8b5e3c', margin: '2px 0 0' }}>${totalStopCost.toFixed(0)} estimated lodging</p>
                : null
            })()}
          </div>
          <button
            onClick={() => setShowAddStop(v => !v)}
            style={{ background: '#f5e6c8', color: '#8b5e3c', border: 'none', borderRadius: 8, padding: '6px 14px', fontSize: 13, fontWeight: 600, cursor: 'pointer' }}
          >
            {showAddStop ? 'Cancel' : '+ Stop'}
          </button>
        </div>

        {showAddStop && (
          <div style={{ background: '#fdf6e8', border: '1px solid #e8d5b0', borderRadius: 12, padding: 16, marginBottom: 16 }}>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
              <div style={{ display: 'flex', gap: 8 }}>
                <input
                  type="text"
                  placeholder="Location (e.g. Zion NP, UT)"
                  value={stopForm.location}
                  onChange={e => setStopForm(p => ({ ...p, location: e.target.value }))}
                  style={{ flex: 2, border: '1px solid #e8d5b0', borderRadius: 10, padding: '10px 12px', fontSize: 14, outline: 'none' }}
                />
                <select
                  value={stopForm.type}
                  onChange={e => setStopForm(p => ({ ...p, type: e.target.value }))}
                  style={{ flex: 1, border: '1px solid #e8d5b0', borderRadius: 10, padding: '10px 12px', fontSize: 14, outline: 'none', background: 'white' }}
                >
                  <option value="campground">Campground</option>
                  <option value="boondock">Boondock</option>
                  <option value="rv_park">RV Park</option>
                  <option value="city">City / Hotel</option>
                  <option value="other">Other</option>
                </select>
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: 8 }}>
                <div>
                  <label style={{ fontSize: 11, color: '#8b5e3c', fontWeight: 600, display: 'block', marginBottom: 4 }}>Arrival</label>
                  <input type="date" value={stopForm.arrival_date} onChange={e => setStopForm(p => ({ ...p, arrival_date: e.target.value }))}
                    style={{ width: '100%', border: '1px solid #e8d5b0', borderRadius: 10, padding: '9px 10px', fontSize: 13, outline: 'none', boxSizing: 'border-box' }} />
                </div>
                <div>
                  <label style={{ fontSize: 11, color: '#8b5e3c', fontWeight: 600, display: 'block', marginBottom: 4 }}>Departure</label>
                  <input type="date" value={stopForm.departure_date} onChange={e => setStopForm(p => ({ ...p, departure_date: e.target.value }))}
                    style={{ width: '100%', border: '1px solid #e8d5b0', borderRadius: 10, padding: '9px 10px', fontSize: 13, outline: 'none', boxSizing: 'border-box' }} />
                </div>
                <div>
                  <label style={{ fontSize: 11, color: '#8b5e3c', fontWeight: 600, display: 'block', marginBottom: 4 }}>$/night</label>
                  <div style={{ position: 'relative' }}>
                    <span style={{ position: 'absolute', left: 10, top: '50%', transform: 'translateY(-50%)', color: '#8b5e3c', fontSize: 13 }}>$</span>
                    <input type="number" placeholder="0" value={stopForm.cost_per_night} onChange={e => setStopForm(p => ({ ...p, cost_per_night: e.target.value }))}
                      style={{ width: '100%', border: '1px solid #e8d5b0', borderRadius: 10, padding: '9px 10px 9px 24px', fontSize: 13, outline: 'none', boxSizing: 'border-box' }} />
                  </div>
                </div>
              </div>
              <input type="text" placeholder="Notes (optional)" value={stopForm.notes} onChange={e => setStopForm(p => ({ ...p, notes: e.target.value }))}
                style={{ border: '1px solid #e8d5b0', borderRadius: 10, padding: '10px 12px', fontSize: 14, outline: 'none' }} />
              <button
                onClick={addStop}
                disabled={savingStop || !stopForm.location.trim()}
                style={{ background: '#2d5a27', color: 'white', border: 'none', borderRadius: 10, padding: '10px', fontSize: 14, fontWeight: 600, cursor: 'pointer', opacity: (!stopForm.location.trim() || savingStop) ? 0.4 : 1 }}
              >
                {savingStop ? 'Saving...' : 'Add stop'}
              </button>
            </div>
          </div>
        )}

        {stops.length === 0 && !showAddStop && (
          <p style={{ fontSize: 14, color: '#b0a090', margin: 0 }}>No stops planned yet. Map out your route.</p>
        )}

        {stops.length > 0 && (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 1 }}>
            {stops.map((s, i) => {
              const nights = s.arrival_date && s.departure_date
                ? Math.max(0, Math.round((new Date(s.departure_date) - new Date(s.arrival_date)) / 86400000))
                : null
              const cost = nights && s.cost_per_night ? nights * Number(s.cost_per_night) : null
              const typeLabels = { campground: 'Campground', boondock: 'Boondock', rv_park: 'RV Park', city: 'City', other: 'Stop' }
              const typeColors = { campground: '#2d5a27', boondock: '#6b9c5e', rv_park: '#4a8c7a', city: '#8b5e3c', other: '#a89070' }
              return (
                <div key={s.id} style={{ display: 'flex', alignItems: 'flex-start', gap: 12, padding: '12px 0', borderBottom: i < stops.length - 1 ? '1px solid #f5e6c8' : 'none' }}>
                  <div style={{ width: 8, height: 8, borderRadius: '50%', background: typeColors[s.type] || '#a89070', marginTop: 6, flexShrink: 0 }} />
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <p style={{ fontSize: 15, fontWeight: 600, color: '#4a3728', margin: 0 }}>{s.location}</p>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 3, flexWrap: 'wrap' }}>
                      <span style={{ fontSize: 11, background: '#f5e6c8', color: typeColors[s.type] || '#a89070', borderRadius: 6, padding: '2px 8px', fontWeight: 600 }}>
                        {typeLabels[s.type] || s.type}
                      </span>
                      {s.arrival_date && (
                        <span style={{ fontSize: 12, color: '#8b5e3c' }}>
                          {s.arrival_date}{s.departure_date ? ` → ${s.departure_date}` : ''}
                          {nights !== null ? ` · ${nights} night${nights !== 1 ? 's' : ''}` : ''}
                        </span>
                      )}
                      {s.notes && <span style={{ fontSize: 12, color: '#b0a090' }}>{s.notes}</span>}
                    </div>
                  </div>
                  <div style={{ textAlign: 'right', flexShrink: 0 }}>
                    {cost != null && <p style={{ fontSize: 14, fontWeight: 700, color: '#4a3728', margin: 0 }}>${cost.toFixed(0)}</p>}
                    {s.cost_per_night && <p style={{ fontSize: 12, color: '#8b5e3c', margin: '2px 0 0' }}>${Number(s.cost_per_night).toFixed(0)}/night</p>}
                  </div>
                  <button
                    onClick={() => deleteStop(s.id)}
                    style={{ background: 'none', border: 'none', color: '#c4b5a5', fontSize: 16, cursor: 'pointer', padding: '0 4px', lineHeight: 1, flexShrink: 0 }}
                    title="Remove stop"
                  >
                    ×
                  </button>
                </div>
              )
            })}
          </div>
        )}
      </div>

      {/* Budget breakdown */}
      <div style={card}>
        <h2 style={{ fontSize: 17, fontWeight: 600, color: '#4a3728', margin: '0 0 16px' }}>Budget breakdown</h2>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          {budgets.map(b => {
            const spent = spentByCategory[b.category] || 0
            const plan = Number(b.planned_amount) || 0
            const catPct = plan > 0 ? Math.min((spent / plan) * 100, 100) : 0
            const catColor = catPct >= 100 ? '#d4622a' : catPct >= 80 ? '#e07440' : '#2d5a27'
            return (
              <div key={b.id}>
                <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 14, marginBottom: 6 }}>
                  <span style={{ color: '#4a3728' }}>{CATEGORY_LABELS[b.category] || b.category}</span>
                  <span style={{ fontWeight: 600, color: spent > plan ? '#d4622a' : '#8b5e3c' }}>
                    ${spent.toFixed(0)} / ${plan.toFixed(0)}
                  </span>
                </div>
                <div style={{ height: 8, background: '#f5e6c8', borderRadius: 99, overflow: 'hidden' }}>
                  <div style={{ height: '100%', width: `${catPct}%`, background: catColor, borderRadius: 99 }} />
                </div>
              </div>
            )
          })}
        </div>
      </div>

      {/* Charts */}
      {spendLines.length > 0 && (
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginBottom: 12 }}>

          {/* Category donut */}
          <div style={{ background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 20 }}>
            <h2 style={{ fontSize: 15, fontWeight: 600, color: '#4a3728', margin: '0 0 12px' }}>By category</h2>
            <ResponsiveContainer width="100%" height={180}>
              <PieChart>
                <Pie data={donutData} cx="50%" cy="50%" innerRadius={45} outerRadius={75} paddingAngle={2} dataKey="value">
                  {donutData.map((entry) => (
                    <Cell key={entry.cat} fill={CATEGORY_COLORS[entry.cat] || '#a89070'} />
                  ))}
                </Pie>
                <Tooltip formatter={(v) => `$${v.toFixed(2)}`} contentStyle={{ borderRadius: 8, border: '1px solid #e8d5b0', fontSize: 13 }} />
              </PieChart>
            </ResponsiveContainer>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 5, marginTop: 8 }}>
              {donutData.map(entry => (
                <div key={entry.cat} style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 12 }}>
                  <div style={{ width: 10, height: 10, borderRadius: 2, background: CATEGORY_COLORS[entry.cat] || '#a89070', flexShrink: 0 }} />
                  <span style={{ color: '#8b5e3c', flex: 1 }}>{entry.name}</span>
                  <span style={{ fontWeight: 600, color: '#4a3728' }}>${entry.value}</span>
                </div>
              ))}
            </div>
          </div>

          {/* Daily spend */}
          <div style={{ background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 20 }}>
            <h2 style={{ fontSize: 15, fontWeight: 600, color: '#4a3728', margin: '0 0 12px' }}>Daily spend</h2>
            <ResponsiveContainer width="100%" height={180}>
              <BarChart data={dailyData} margin={{ top: 0, right: 0, left: -20, bottom: 0 }}>
                <CartesianGrid strokeDasharray="3 3" stroke="#f5e6c8" vertical={false} />
                <XAxis dataKey="date" tick={{ fontSize: 10, fill: '#8b5e3c' }} tickLine={false} axisLine={false} />
                <YAxis tick={{ fontSize: 10, fill: '#8b5e3c' }} tickLine={false} axisLine={false} tickFormatter={v => `$${v}`} />
                <Tooltip formatter={(v) => [`$${v.toFixed(2)}`, 'Spent']} contentStyle={{ borderRadius: 8, border: '1px solid #e8d5b0', fontSize: 13 }} />
                <Bar dataKey="amount" fill="#2d5a27" radius={[4, 4, 0, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>

        </div>
      )}

      {/* Expenses */}
      <div style={card}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: showResetConfirm ? 12 : 16 }}>
          <h2 style={{ fontSize: 17, fontWeight: 600, color: '#4a3728', margin: 0 }}>Expenses</h2>
          <div style={{ display: 'flex', gap: 8 }}>
            <button onClick={() => setShowResetConfirm(v => !v)} style={{
              background: 'white', color: '#b0a090', border: '1px solid #e8d5b0',
              borderRadius: 8, padding: '6px 12px', fontSize: 12, fontWeight: 600, cursor: 'pointer'
            }}>
              Reset
            </button>
            <button onClick={() => setShowAddCash(true)} style={{
              background: '#f5e6c8', color: '#8b5e3c', border: 'none',
              borderRadius: 8, padding: '6px 14px', fontSize: 13, fontWeight: 600, cursor: 'pointer'
            }}>
              + Cash
            </button>
          </div>
        </div>

        {showResetConfirm && (
          <div style={{ background: '#fff5f0', border: '1px solid #f5c5b0', borderRadius: 12, padding: '14px 16px', marginBottom: 16 }}>
            <p style={{ fontSize: 13, fontWeight: 700, color: '#d4622a', margin: '0 0 4px' }}>Re-pull bank transactions?</p>
            <p style={{ fontSize: 12, color: '#8b5e3c', margin: '0 0 12px' }}>
              Resets the Plaid sync cursor and re-pulls your full history, then re-claims this trip's window. Nothing is deleted — stored transactions and manual cash entries are kept.
            </p>
            <div style={{ display: 'flex', gap: 8 }}>
              <button onClick={resetTransactions} disabled={resetting} style={{
                flex: 1, background: '#d4622a', color: 'white', border: 'none',
                borderRadius: 10, padding: '9px', fontSize: 13, fontWeight: 600, cursor: 'pointer',
                opacity: resetting ? 0.6 : 1
              }}>
                {resetting ? 'Re-pulling...' : 'Yes, re-pull'}
              </button>
              <button onClick={() => setShowResetConfirm(false)} style={{
                flex: 1, background: '#f5e6c8', color: '#8b5e3c', border: 'none',
                borderRadius: 10, padding: '9px', fontSize: 13, fontWeight: 600, cursor: 'pointer'
              }}>
                Cancel
              </button>
            </div>
          </div>
        )}

        {showAddCash && (
          <div style={{ background: '#fdf6e8', border: '1px solid #e8d5b0', borderRadius: 12, padding: 16, marginBottom: 16 }}>
            <p style={{ fontSize: 13, fontWeight: 600, color: '#8b5e3c', marginBottom: 10 }}>Add cash purchase</p>
            <div style={{ display: 'flex', gap: 8, marginBottom: 8 }}>
              <div style={{ position: 'relative', flex: 1 }}>
                <span style={{ position: 'absolute', left: 12, top: '50%', transform: 'translateY(-50%)', color: '#8b5e3c' }}>$</span>
                <input type="number" placeholder="0.00" value={cashAmount} onChange={e => setCashAmount(e.target.value)} autoFocus
                  style={{ width: '100%', border: '1px solid #e8d5b0', borderRadius: 10, padding: '10px 12px 10px 28px', fontSize: 15, outline: 'none', boxSizing: 'border-box' }} />
              </div>
              <select value={cashCategory} onChange={e => setCashCategory(e.target.value)}
                style={{ border: '1px solid #e8d5b0', borderRadius: 10, padding: '10px 12px', fontSize: 14, outline: 'none', background: 'white' }}>
                {Object.entries(CATEGORY_LABELS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
              </select>
            </div>
            <input type="text" placeholder="Note (optional)" value={cashNote} onChange={e => setCashNote(e.target.value)}
              style={{ width: '100%', border: '1px solid #e8d5b0', borderRadius: 10, padding: '10px 12px', fontSize: 14, outline: 'none', boxSizing: 'border-box', marginBottom: 10 }} />
            <div style={{ display: 'flex', gap: 8 }}>
              <button onClick={addCashExpense} disabled={addingCash || !cashAmount}
                style={{ flex: 1, background: '#2d5a27', color: 'white', border: 'none', borderRadius: 10, padding: '10px', fontSize: 14, fontWeight: 600, cursor: 'pointer', opacity: (!cashAmount || addingCash) ? 0.4 : 1 }}>
                {addingCash ? 'Adding...' : 'Add'}
              </button>
              <button onClick={() => setShowAddCash(false)}
                style={{ flex: 1, background: '#f5e6c8', color: '#8b5e3c', border: 'none', borderRadius: 10, padding: '10px', fontSize: 14, fontWeight: 600, cursor: 'pointer' }}>
                Cancel
              </button>
            </div>
          </div>
        )}

        {lineItems.length > 0 && (
          <div style={{ display: 'flex', gap: 8, marginBottom: 14, flexWrap: 'wrap' }}>
            <input
              type="text"
              placeholder="Search merchant..."
              value={filterSearch}
              onChange={e => setFilterSearch(e.target.value)}
              style={{ flex: '1 1 140px', border: '1px solid #e8d5b0', borderRadius: 10, padding: '8px 12px', fontSize: 13, outline: 'none', minWidth: 0 }}
            />
            <select
              value={filterCategory}
              onChange={e => setFilterCategory(e.target.value)}
              style={{ flex: '1 1 120px', border: '1px solid #e8d5b0', borderRadius: 10, padding: '8px 12px', fontSize: 13, outline: 'none', background: 'white', color: filterCategory ? '#4a3728' : '#b0a090' }}
            >
              <option value="">All categories</option>
              {Object.entries(CATEGORY_LABELS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </select>
            <select
              value={filterSort}
              onChange={e => setFilterSort(e.target.value)}
              style={{ flex: '1 1 110px', border: '1px solid #e8d5b0', borderRadius: 10, padding: '8px 12px', fontSize: 13, outline: 'none', background: 'white', color: filterSort ? '#4a3728' : '#b0a090' }}
            >
              <option value="">Date order</option>
              <option value="desc">Highest first</option>
              <option value="asc">Lowest first</option>
            </select>
            {(filterSearch || filterCategory || filterSort) && (
              <button
                onClick={() => { setFilterSearch(''); setFilterCategory(''); setFilterSort('') }}
                style={{ border: '1px solid #e8d5b0', borderRadius: 10, padding: '8px 12px', fontSize: 13, background: 'white', color: '#8b5e3c', cursor: 'pointer', whiteSpace: 'nowrap' }}
              >
                Clear
              </button>
            )}
          </div>
        )}

        {lineItems.length === 0 ? (
          <p style={{ fontSize: 14, color: '#8b5e3c', textAlign: 'center', padding: '24px 0' }}>
            No expenses yet. Connect your bank or add cash purchases.
          </p>
        ) : filteredItems.length === 0 ? (
          <p style={{ fontSize: 14, color: '#8b5e3c', textAlign: 'center', padding: '24px 0' }}>
            No expenses match your filters.
          </p>
        ) : (
          <div style={{ maxHeight: 600, overflowY: 'auto' }}>
            {(() => {
              const reviewed = filteredItems.filter(it => it.reviewed)
              const pending = filteredItems.filter(it => !it.reviewed)

              const sourceBadge = (it) => (
                it.source === 'plaid'
                  ? <span style={{ fontSize: 11, background: '#e8f0e4', color: '#2d5a27', borderRadius: 6, padding: '1px 7px', fontWeight: 600 }}>Bank</span>
                  : <span style={{ fontSize: 11, background: '#f5e6c8', color: '#8b5e3c', borderRadius: 6, padding: '1px 7px', fontWeight: 600 }}>Cash</span>
              )

              const renderRow = (it, i, arr) => {
                const isEditing = it.source === 'manual' && editingId === it.id
                const isExpanded = it.source === 'plaid' && expandedTxnId === it.id
                const onRowClick = () => {
                  if (it.source === 'manual') {
                    isEditing ? setEditingId(null) : openEdit(it.raw)
                  } else if (isExpanded) {
                    setExpandedTxnId(null)
                  } else {
                    // Prime the split inputs from the row so the panel opens ready to edit
                    setExpandedTxnId(it.id)
                    setEditShare(it.amount.toFixed(2))
                    setEditNote(it.adjustmentNote || '')
                    setEditError('')
                  }
                }
                return (
                <div key={it.key} style={{ borderBottom: i < arr.length - 1 ? '1px solid #f5e6c8' : 'none' }}>
                  {/* Row */}
                  <div
                    onClick={onRowClick}
                    style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '12px 0', cursor: 'pointer' }}
                  >
                    <div style={{ flex: 1, minWidth: 0 }}>
                      <p style={{ fontSize: 15, color: '#4a3728', margin: 0 }}>{it.title}</p>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 2, flexWrap: 'wrap' }}>
                        {sourceBadge(it)}
                        <p style={{ fontSize: 12, color: '#8b5e3c', margin: 0 }}>{CATEGORY_LABELS[it.category] || it.category} · {it.date}</p>
                        {it.pending && (
                          <span style={{ fontSize: 11, background: '#fdf0d5', color: '#b07a2a', borderRadius: 6, padding: '1px 7px', fontWeight: 600 }}>Pending</span>
                        )}
                        {it.adjusted && (
                          <span style={{ fontSize: 11, background: '#f5e6c8', color: '#8b5e3c', borderRadius: 6, padding: '1px 7px', fontWeight: 600 }}>
                            {it.adjustmentNote || 'Adjusted'}
                          </span>
                        )}
                      </div>
                    </div>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginLeft: 12, flexShrink: 0 }}>
                      <div style={{ textAlign: 'right' }}>
                        <p style={{ fontSize: 15, fontWeight: 700, color: '#4a3728', margin: 0 }}>${it.amount.toFixed(2)}</p>
                        {it.adjusted && (
                          <p style={{ fontSize: 11, color: '#b0a090', margin: '1px 0 0', textDecoration: 'line-through' }}>
                            ${it.original.toFixed(2)}
                          </p>
                        )}
                      </div>
                      {/* × removes the row: hard-delete for manual, unassign-from-trip for Plaid */}
                      <button
                        onClick={ev => {
                          ev.stopPropagation()
                          it.source === 'manual' ? deleteExpense(it.id) : updateTransactionAssignment(it.id, null)
                        }}
                        disabled={txnBusyId === it.id}
                        style={{ background: 'none', border: 'none', color: '#c4b5a5', fontSize: 18, cursor: 'pointer', padding: '0 2px', lineHeight: 1, flexShrink: 0, opacity: txnBusyId === it.id ? 0.4 : 1 }}
                        title={it.source === 'manual' ? 'Delete expense' : 'Remove from this trip'}
                      >×</button>
                    </div>
                  </div>

                  {/* Plaid inline panel — the raw amount is an immutable mirror, but the
                      split/reimbursed overlay (adjusted_amount, via the API) is editable,
                      matching the manual-expense adjust flow. */}
                  {isExpanded && (
                    <div style={{ background: '#fdf6e8', border: '1px solid #e8d5b0', borderRadius: 12, padding: 16, marginBottom: 12 }}>
                      <p style={{ fontSize: 12, color: '#8b5e3c', margin: '0 0 10px', fontWeight: 600 }}>
                        Bank transaction · original ${it.original.toFixed(2)} · {it.date}
                        {it.reviewed ? ' · reviewed' : ''}
                      </p>

                      {/* Category — re-categorize this bank transaction (persists via the assign API,
                          works even after it's been reviewed) */}
                      <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
                        <label style={{ fontSize: 12, color: '#8b5e3c', fontWeight: 600, flexShrink: 0 }}>Category</label>
                        <select
                          value={it.category || 'misc'}
                          onChange={e => changeTransactionCategory(it.id, e.target.value)}
                          disabled={txnBusyId === it.id}
                          style={{ flex: 1, border: '1px solid #e8d5b0', borderRadius: 10, padding: '8px 10px', fontSize: 14, background: 'white', color: '#4a3728', outline: 'none', opacity: txnBusyId === it.id ? 0.6 : 1 }}
                        >
                          {Object.entries(CATEGORY_LABELS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
                        </select>
                      </div>

                      {/* Quick split presets — computed off the ORIGINAL bank amount */}
                      <div style={{ display: 'flex', gap: 6, marginBottom: 10 }}>
                        {['Split 50/50', 'Reimbursed', 'Split 70/30'].map(preset => (
                          <button
                            key={preset}
                            onClick={() => {
                              if (preset === 'Split 50/50') {
                                setEditShare((it.original / 2).toFixed(2))
                                setEditNote('Split 50/50')
                              } else if (preset === 'Split 70/30') {
                                setEditShare((it.original * 0.7).toFixed(2))
                                setEditNote('Split 70/30')
                              } else {
                                setEditShare('0.00')
                                setEditNote('Reimbursed')
                              }
                            }}
                            style={{ fontSize: 12, background: 'white', border: '1px solid #e8d5b0', borderRadius: 8, padding: '5px 10px', cursor: 'pointer', color: '#4a3728' }}
                          >
                            {preset}
                          </button>
                        ))}
                      </div>

                      <div style={{ display: 'flex', gap: 8, marginBottom: 8 }}>
                        <div style={{ position: 'relative', flex: 1 }}>
                          <span style={{ position: 'absolute', left: 12, top: '50%', transform: 'translateY(-50%)', color: '#8b5e3c', fontSize: 14 }}>$</span>
                          <input
                            type="number"
                            placeholder="Your share"
                            value={editShare}
                            onChange={e => setEditShare(e.target.value)}
                            style={{ width: '100%', border: '1px solid #e8d5b0', borderRadius: 10, padding: '9px 12px 9px 26px', fontSize: 14, outline: 'none', boxSizing: 'border-box' }}
                          />
                        </div>
                        <input
                          type="text"
                          placeholder="Reason (e.g. Split with Stefanie)"
                          value={editNote}
                          onChange={e => setEditNote(e.target.value)}
                          style={{ flex: 2, border: '1px solid #e8d5b0', borderRadius: 10, padding: '9px 12px', fontSize: 14, outline: 'none' }}
                        />
                      </div>

                      {editError && (
                        <p style={{ fontSize: 12, color: '#d4622a', background: '#fff5f0', borderRadius: 8, padding: '8px 12px', marginBottom: 8 }}>
                          {editError}
                        </p>
                      )}

                      <div style={{ display: 'flex', gap: 8, marginBottom: 8 }}>
                        <button
                          onClick={() => savePlaidAdjustment(it.raw)}
                          disabled={savingEdit}
                          style={{ flex: 1, background: '#2d5a27', color: 'white', border: 'none', borderRadius: 10, padding: '9px', fontSize: 14, fontWeight: 600, cursor: 'pointer', opacity: savingEdit ? 0.6 : 1 }}
                        >
                          {savingEdit ? 'Saving...' : 'Save split'}
                        </button>
                        {it.adjusted && (
                          <button
                            onClick={() => clearPlaidAdjustment(it.raw)}
                            style={{ flex: 1, background: 'white', border: '1px solid #e8d5b0', color: '#8b5e3c', borderRadius: 10, padding: '9px', fontSize: 14, fontWeight: 600, cursor: 'pointer' }}
                          >
                            Remove adjustment
                          </button>
                        )}
                      </div>

                      <div style={{ display: 'flex', gap: 8 }}>
                        {!it.reviewed && (
                          <button
                            onClick={() => updateTransactionAssignment(it.id, id)}
                            disabled={txnBusyId === it.id}
                            style={{ flex: 1, background: '#2d5a27', color: 'white', border: 'none', borderRadius: 10, padding: '9px', fontSize: 14, fontWeight: 600, cursor: 'pointer', opacity: txnBusyId === it.id ? 0.6 : 1 }}
                          >
                            {txnBusyId === it.id ? 'Saving...' : 'Confirm ✓'}
                          </button>
                        )}
                        <button
                          onClick={() => updateTransactionAssignment(it.id, null)}
                          disabled={txnBusyId === it.id}
                          style={{ flex: 1, background: 'white', border: '1px solid #e8d5b0', color: '#8b5e3c', borderRadius: 10, padding: '9px', fontSize: 14, fontWeight: 600, cursor: 'pointer', opacity: txnBusyId === it.id ? 0.6 : 1 }}
                        >
                          Remove from trip
                        </button>
                        <button
                          onClick={() => setExpandedTxnId(null)}
                          style={{ flex: 1, background: '#f5e6c8', color: '#8b5e3c', border: 'none', borderRadius: 10, padding: '9px', fontSize: 14, fontWeight: 600, cursor: 'pointer' }}
                        >
                          Cancel
                        </button>
                      </div>
                    </div>
                  )}

                  {/* Manual inline edit panel (adjust flow) */}
                  {isEditing && (
                    <div style={{ background: '#fdf6e8', border: '1px solid #e8d5b0', borderRadius: 12, padding: 16, marginBottom: 12 }}>
                      <p style={{ fontSize: 12, color: '#8b5e3c', margin: '0 0 10px', fontWeight: 600 }}>
                        Original: ${it.original.toFixed(2)} · {it.title}
                      </p>

                      {/* Quick preset buttons */}
                      <div style={{ display: 'flex', gap: 6, marginBottom: 10 }}>
                        {['Split 50/50', 'Reimbursed', 'Split 70/30'].map(preset => (
                          <button
                            key={preset}
                            onClick={() => {
                              if (preset === 'Split 50/50') {
                                setEditShare((it.original / 2).toFixed(2))
                                setEditNote('Split 50/50')
                              } else if (preset === 'Split 70/30') {
                                setEditShare((it.original * 0.7).toFixed(2))
                                setEditNote('Split 70/30')
                              } else {
                                setEditShare('0.00')
                                setEditNote('Reimbursed')
                              }
                            }}
                            style={{ fontSize: 12, background: 'white', border: '1px solid #e8d5b0', borderRadius: 8, padding: '5px 10px', cursor: 'pointer', color: '#4a3728' }}
                          >
                            {preset}
                          </button>
                        ))}
                      </div>

                      <div style={{ display: 'flex', gap: 8, marginBottom: 8 }}>
                        <div style={{ position: 'relative', flex: 1 }}>
                          <span style={{ position: 'absolute', left: 12, top: '50%', transform: 'translateY(-50%)', color: '#8b5e3c', fontSize: 14 }}>$</span>
                          <input
                            type="number"
                            placeholder="Your share"
                            value={editShare}
                            onChange={e => setEditShare(e.target.value)}
                            style={{ width: '100%', border: '1px solid #e8d5b0', borderRadius: 10, padding: '9px 12px 9px 26px', fontSize: 14, outline: 'none', boxSizing: 'border-box' }}
                          />
                        </div>
                        <input
                          type="text"
                          placeholder="Reason (e.g. Split with Stefanie)"
                          value={editNote}
                          onChange={e => setEditNote(e.target.value)}
                          style={{ flex: 2, border: '1px solid #e8d5b0', borderRadius: 10, padding: '9px 12px', fontSize: 14, outline: 'none' }}
                        />
                      </div>

                      {editError && (
                        <p style={{ fontSize: 12, color: '#d4622a', background: '#fff5f0', borderRadius: 8, padding: '8px 12px', marginBottom: 8 }}>
                          {editError}
                        </p>
                      )}
                      <div style={{ display: 'flex', gap: 8 }}>
                        <button
                          onClick={() => saveAdjustment(it.raw)}
                          disabled={savingEdit}
                          style={{ flex: 1, background: '#2d5a27', color: 'white', border: 'none', borderRadius: 10, padding: '9px', fontSize: 14, fontWeight: 600, cursor: 'pointer', opacity: savingEdit ? 0.6 : 1 }}
                        >
                          {savingEdit ? 'Saving...' : 'Save'}
                        </button>
                        {it.adjusted && (
                          <button
                            onClick={() => clearAdjustment(it.raw)}
                            style={{ flex: 1, background: 'white', border: '1px solid #e8d5b0', color: '#8b5e3c', borderRadius: 10, padding: '9px', fontSize: 14, fontWeight: 600, cursor: 'pointer' }}
                          >
                            Remove adjustment
                          </button>
                        )}
                        <button
                          onClick={() => setEditingId(null)}
                          style={{ flex: 1, background: '#f5e6c8', color: '#8b5e3c', border: 'none', borderRadius: 10, padding: '9px', fontSize: 14, fontWeight: 600, cursor: 'pointer' }}
                        >
                          Cancel
                        </button>
                      </div>
                    </div>
                  )}
                </div>
                )
              }
              return (
                <>
                  {reviewed.map((it, i) => renderRow(it, i, reviewed))}
                  {pending.length > 0 && (
                    <>
                      {reviewed.length > 0 && (
                        <div style={{ background: '#fdf6e8', border: '1px solid #e8d5b0', borderRadius: 8, padding: '7px 14px', margin: '14px 0 6px', display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
                          <span style={{ fontSize: 13, fontWeight: 700, color: '#8b5e3c' }}>
                            Pending review
                          </span>
                          <span style={{ fontSize: 12, fontWeight: 600, color: '#b0a090' }}>
                            {pending.length} transaction{pending.length !== 1 ? 's' : ''}
                          </span>
                        </div>
                      )}
                      {pending.map((it, i) => renderRow(it, i, pending))}
                    </>
                  )}
                </>
              )
            })()}
          </div>
        )}
      </div>

      {/* Other transactions in this trip's date range — on other trips or unassigned.
          Reviewed rows are never auto-moved, so this is the only place they can be
          pulled into an overlapping trip. */}
      {windowTxns.length > 0 && (
        <div style={card}>
          <h2 style={{ fontSize: 17, fontWeight: 600, color: '#4a3728', margin: '0 0 4px' }}>
            Other transactions in this date range
          </h2>
          <p style={{ fontSize: 12, color: '#8b5e3c', margin: '0 0 12px' }}>
            {windowTxns.length} transaction{windowTxns.length !== 1 ? 's' : ''} from your bank in this window that {windowTxns.length !== 1 ? 'are' : 'is'} on another trip or unassigned.
          </p>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 1, maxHeight: 400, overflowY: 'auto' }}>
            {windowTxns.map((t, i) => (
              <div key={t.id} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10, padding: '10px 0', borderBottom: i < windowTxns.length - 1 ? '1px solid #f5e6c8' : 'none' }}>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <p style={{ fontSize: 14, color: '#4a3728', margin: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>
                    {t.merchant_name || t.name || CATEGORY_LABELS[t.category] || 'Transaction'}
                  </p>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 2 }}>
                    <span style={{
                      fontSize: 11, borderRadius: 6, padding: '1px 7px', fontWeight: 600,
                      background: t.trip_id ? '#ede4f5' : '#f5e6c8',
                      color: t.trip_id ? '#7a5c99' : '#8b5e3c',
                    }}>
                      {t.trip_id ? (tripNames[t.trip_id] || 'Another trip') : 'Unassigned'}
                    </span>
                    <span style={{ fontSize: 12, color: '#8b5e3c' }}>{t.transaction_date}</span>
                  </div>
                </div>
                <span style={{ fontSize: 14, fontWeight: 700, color: '#4a3728', flexShrink: 0 }}>
                  ${effectiveAmount(t).toFixed(2)}
                </span>
                <button
                  onClick={() => claimTransaction(t)}
                  disabled={txnBusyId === t.id}
                  style={{ background: '#2d5a27', color: 'white', border: 'none', borderRadius: 8, padding: '6px 12px', fontSize: 12, fontWeight: 600, cursor: 'pointer', flexShrink: 0, opacity: txnBusyId === t.id ? 0.6 : 1 }}
                >
                  {txnBusyId === t.id ? '...' : 'Move here'}
                </button>
              </div>
            ))}
          </div>
        </div>
      )}
      <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
    </div>
  )
}
