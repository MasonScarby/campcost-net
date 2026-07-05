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

export default function TripDetail() {
  const { id } = useParams()
  const { user } = useAuth()
  const [trip, setTrip] = useState(null)
  const [budgets, setBudgets] = useState([])
  const [expenses, setExpenses] = useState([])
  const [loading, setLoading] = useState(true)
  const [showAddCash, setShowAddCash] = useState(false)
  const [cashAmount, setCashAmount] = useState('')
  const [cashCategory, setCashCategory] = useState('misc')
  const [cashNote, setCashNote] = useState('')
  const [addingCash, setAddingCash] = useState(false)
  const [syncStatus, setSyncStatus] = useState('idle') // idle | syncing | done
  const [editingId, setEditingId] = useState(null)
  const [editShare, setEditShare] = useState('')
  const [editNote, setEditNote] = useState('')
  const [savingEdit, setSavingEdit] = useState(false)
  const [editError, setEditError] = useState('')

  // Trip date editing
  const [editingDates, setEditingDates] = useState(false)
  const [dateForm, setDateForm] = useState({ start_date: '', end_date: '' })
  const [savingDates, setSavingDates] = useState(false)

  // Reset transactions
  const [showResetConfirm, setShowResetConfirm] = useState(false)
  const [resetting, setResetting] = useState(false)

  // Filters
  const [filterSearch, setFilterSearch] = useState('')
  const [filterCategory, setFilterCategory] = useState('')
  const [filterSort, setFilterSort] = useState('')

  // Campground stops
  const [stops, setStops] = useState([])
  const [showAddStop, setShowAddStop] = useState(false)
  const [stopForm, setStopForm] = useState({ location: '', type: 'campground', arrival_date: '', departure_date: '', cost_per_night: '', notes: '' })
  const [savingStop, setSavingStop] = useState(false)

  useEffect(() => {
    const fetchAll = async () => {
      const [{ data: t }, { data: b }, { data: e }, { data: s }] = await Promise.all([
        supabase.from('trips').select('*').eq('id', id).eq('user_id', user.id).single(),
        supabase.from('budget_categories').select('*').eq('trip_id', id),
        supabase.from('expenses').select('*').eq('trip_id', id).order('expense_date', { ascending: false }),
        supabase.from('trip_stops').select('*').eq('trip_id', id).order('arrival_date', { ascending: true }),
      ])
      setTrip(t)
      setBudgets(b ?? [])
      setExpenses(e ?? [])
      setStops(s ?? [])
      setLoading(false)
    }
    fetchAll()
  }, [id, user.id])

  // Returns true if expense_date falls within trip date range ± 2 days.
  // Compares YYYY-MM-DD strings directly (lexicographic == chronological) to
  // avoid timezone bugs — new Date('2026-06-11') parses as UTC midnight which
  // can shift to the wrong local date in US timezones.
  const isInRange = (expenseDate, t) => {
    if (!expenseDate) return false
    const BUFFER = 2
    // Use noon UTC when doing arithmetic so DST/UTC-offset never flips the date
    const shiftDate = (dateStr, days) => {
      const d = new Date(`${dateStr}T12:00:00Z`)
      d.setUTCDate(d.getUTCDate() + days)
      return d.toISOString().split('T')[0]
    }
    // Normalize: Supabase may return full ISO timestamp, grab just the date part
    const dateOnly = expenseDate.split('T')[0]
    if (t?.start_date && dateOnly < shiftDate(t.start_date, -BUFFER)) return false
    if (t?.end_date   && dateOnly > shiftDate(t.end_date,   BUFFER))  return false
    return true
  }

  const fetchExpenses = async () => {
    const { data: e } = await supabase
      .from('expenses').select('*').eq('trip_id', id).order('expense_date', { ascending: false })
    setExpenses(e ?? [])
  }

  const handleSync = async () => {
    setSyncStatus('syncing')
    try {
      const res = await backendFetch(`/api/plaid/sync?trip_id=${id}`, { method: 'POST' })
      const result = await res.json()
      console.log('[CampCost sync] fetched:', result.transactionsFetched, '| upserted:', result.expensesUpserted, '| skipped:', result.expensesSkipped)
      await fetchExpenses()
      setSyncStatus('done')
      setTimeout(() => setSyncStatus('idle'), 3000)
    } catch {
      setSyncStatus('idle')
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

  const addStop = async () => {
    if (!stopForm.location.trim()) return
    setSavingStop(true)
    const nights = stopForm.arrival_date && stopForm.departure_date
      ? Math.max(0, Math.round((new Date(stopForm.departure_date) - new Date(stopForm.arrival_date)) / 86400000))
      : null
    const { data, error } = await supabase.from('trip_stops').insert({
      trip_id: id,
      user_id: user.id,
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
    const { error } = await supabase.from('trips').update({
      start_date: dateForm.start_date || null,
      end_date: dateForm.end_date || null,
    }).eq('id', id)
    if (!error) {
      setTrip(prev => ({ ...prev, start_date: dateForm.start_date || null, end_date: dateForm.end_date || null }))
      setEditingDates(false)
      // Auto-sync with this trip's date range so expanded dates pull new transactions
      try {
        setSyncStatus('syncing')
        await backendFetch(`/api/plaid/sync?trip_id=${id}`, { method: 'POST' })
        await fetchExpenses()
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

  const resetTransactions = async () => {
    setResetting(true)
    // Delete all Plaid-sourced expenses for this trip
    await supabase.from('expenses').delete().eq('trip_id', id).eq('source', 'plaid')
    // Null out the sync cursor so next sync fetches full 90-day history
    await supabase.from('plaid_connections').update({ last_sync_at: null }).eq('user_id', user.id)
    setShowResetConfirm(false)
    // Fresh trip-specific sync so everything comes back on the right trip
    try {
      setSyncStatus('syncing')
      await backendFetch(`/api/plaid/sync?trip_id=${id}`, { method: 'POST' })
      await fetchExpenses()
      setSyncStatus('done')
      setTimeout(() => setSyncStatus('idle'), 3000)
    } catch {
      setSyncStatus('idle')
    }
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

  // Only count reviewed expenses that fall within the trip date range
  const reviewedExpenses = expenses.filter(e => e.reviewed && isInRange(e.expense_date, trip))
  const totalSpent = reviewedExpenses.reduce((s, e) => s + effectiveAmount(e), 0)
  const unreviewedInRange = expenses.filter(e => !e.reviewed && isInRange(e.expense_date, trip))
  const unreviewedTotal = unreviewedInRange.reduce((s, e) => s + effectiveAmount(e), 0)
  const totalBudget = Number(trip.total_budget) || 0
  const pct = totalBudget > 0 ? Math.min((totalSpent / totalBudget) * 100, 100) : 0
  const spentByCategory = reviewedExpenses.reduce((acc, e) => {
    acc[e.category] = (acc[e.category] || 0) + effectiveAmount(e); return acc
  }, {})
  const nights = trip.start_date && trip.end_date
    ? Math.max(1, Math.round((new Date(trip.end_date) - new Date(trip.start_date)) / 86400000)) : null
  const costPerNight = nights ? (totalSpent / nights).toFixed(2) : null
  const costPerMile = trip.total_miles ? (totalSpent / trip.total_miles).toFixed(2) : null
  const barColor = pct >= 90 ? '#d4622a' : pct >= 70 ? '#e07440' : '#2d5a27'

  const unreviewedCount = unreviewedInRange.length

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

  const filteredExpenses = expenses
    .filter(e => e.adjustment_note !== 'skipped')
    .filter(e => isInRange(e.expense_date, trip))
    .filter(e => !filterCategory || e.category === filterCategory)
    .filter(e => {
      if (!filterSearch) return true
      const term = filterSearch.toLowerCase()
      return (e.merchant_name || '').toLowerCase().includes(term) ||
             (e.note || '').toLowerCase().includes(term)
    })
    .sort((a, b) => {
      if (filterSort === 'asc') return effectiveAmount(a) - effectiveAmount(b)
      if (filterSort === 'desc') return effectiveAmount(b) - effectiveAmount(a)
      return 0
    })

  const donutData = Object.entries(spentByCategory)
    .filter(([, v]) => v > 0)
    .map(([cat, value]) => ({ name: CATEGORY_LABELS[cat] || cat, value: Number(value.toFixed(2)), cat }))

  const dailyData = Object.entries(
    reviewedExpenses.reduce((acc, e) => {
      acc[e.expense_date] = (acc[e.expense_date] || 0) + effectiveAmount(e)
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
              ${unreviewedTotal.toFixed(2)} pending review
            </p>
            <p style={{ fontSize: 12, color: '#8b5e3c', margin: '2px 0 0' }}>
              {unreviewedCount} transaction{unreviewedCount !== 1 ? 's' : ''} not yet counted in totals
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
          {syncStatus === 'done' && (
            <span style={{ fontSize: 12, color: '#2d5a27', fontWeight: 600 }}>Up to date</span>
          )}
        </div>
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
      {expenses.length > 0 && (
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
            <p style={{ fontSize: 13, fontWeight: 700, color: '#d4622a', margin: '0 0 4px' }}>Delete all Plaid transactions and re-import?</p>
            <p style={{ fontSize: 12, color: '#8b5e3c', margin: '0 0 12px' }}>
              Removes all bank-synced expenses for this trip, resets the sync cursor, then re-imports the last 90 days fresh. Manual cash entries are kept.
            </p>
            <div style={{ display: 'flex', gap: 8 }}>
              <button onClick={resetTransactions} disabled={resetting} style={{
                flex: 1, background: '#d4622a', color: 'white', border: 'none',
                borderRadius: 10, padding: '9px', fontSize: 13, fontWeight: 600, cursor: 'pointer',
                opacity: resetting ? 0.6 : 1
              }}>
                {resetting ? 'Resetting...' : 'Yes, reset & re-import'}
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

        {expenses.length > 0 && (
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

        {expenses.length === 0 ? (
          <p style={{ fontSize: 14, color: '#8b5e3c', textAlign: 'center', padding: '24px 0' }}>
            No expenses yet. Connect your bank or add cash purchases.
          </p>
        ) : filteredExpenses.length === 0 ? (
          <p style={{ fontSize: 14, color: '#8b5e3c', textAlign: 'center', padding: '24px 0' }}>
            No expenses match your filters.
          </p>
        ) : (
          <div style={{ maxHeight: 600, overflowY: 'auto' }}>
            {(() => {
              const reviewed = filteredExpenses.filter(e => e.reviewed)
              const pending = filteredExpenses.filter(e => !e.reviewed)
              const renderRow = (e, i, arr) => {
                const adjusted = e.adjusted_amount != null
                const displayed = effectiveAmount(e)
                const isEditing = editingId === e.id
                return (
                <div key={e.id} style={{ borderBottom: i < arr.length - 1 ? '1px solid #f5e6c8' : 'none' }}>
                  {/* Row */}
                  <div
                    onClick={() => isEditing ? setEditingId(null) : openEdit(e)}
                    style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', padding: '12px 0', cursor: 'pointer' }}
                  >
                    <div style={{ flex: 1, minWidth: 0 }}>
                      <p style={{ fontSize: 15, color: '#4a3728', margin: 0 }}>{e.merchant_name || e.note || CATEGORY_LABELS[e.category]}</p>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 2, flexWrap: 'wrap' }}>
                        <p style={{ fontSize: 12, color: '#8b5e3c', margin: 0 }}>{CATEGORY_LABELS[e.category]} · {e.expense_date}</p>
                        {adjusted && (
                          <span style={{ fontSize: 11, background: '#f5e6c8', color: '#8b5e3c', borderRadius: 6, padding: '1px 7px', fontWeight: 600 }}>
                            {e.adjustment_note || 'Adjusted'}
                          </span>
                        )}
                      </div>
                    </div>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginLeft: 12, flexShrink: 0 }}>
                      <div style={{ textAlign: 'right' }}>
                        <p style={{ fontSize: 15, fontWeight: 700, color: '#4a3728', margin: 0 }}>${displayed.toFixed(2)}</p>
                        {adjusted && (
                          <p style={{ fontSize: 11, color: '#b0a090', margin: '1px 0 0', textDecoration: 'line-through' }}>
                            ${Number(e.amount).toFixed(2)}
                          </p>
                        )}
                      </div>
                      <button
                        onClick={ev => { ev.stopPropagation(); deleteExpense(e.id) }}
                        style={{ background: 'none', border: 'none', color: '#c4b5a5', fontSize: 18, cursor: 'pointer', padding: '0 2px', lineHeight: 1, flexShrink: 0 }}
                        title="Delete expense"
                      >×</button>
                    </div>
                  </div>

                  {/* Inline edit panel */}
                  {isEditing && (
                    <div style={{ background: '#fdf6e8', border: '1px solid #e8d5b0', borderRadius: 12, padding: 16, marginBottom: 12 }}>
                      <p style={{ fontSize: 12, color: '#8b5e3c', margin: '0 0 10px', fontWeight: 600 }}>
                        Original: ${Number(e.amount).toFixed(2)} · {e.merchant_name || CATEGORY_LABELS[e.category]}
                      </p>

                      {/* Quick preset buttons */}
                      <div style={{ display: 'flex', gap: 6, marginBottom: 10 }}>
                        {['Split 50/50', 'Reimbursed', 'Split 70/30'].map(preset => (
                          <button
                            key={preset}
                            onClick={() => {
                              if (preset === 'Split 50/50') {
                                setEditShare((Number(e.amount) / 2).toFixed(2))
                                setEditNote('Split 50/50')
                              } else if (preset === 'Split 70/30') {
                                setEditShare((Number(e.amount) * 0.7).toFixed(2))
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
                          onClick={() => saveAdjustment(e)}
                          disabled={savingEdit}
                          style={{ flex: 1, background: '#2d5a27', color: 'white', border: 'none', borderRadius: 10, padding: '9px', fontSize: 14, fontWeight: 600, cursor: 'pointer', opacity: savingEdit ? 0.6 : 1 }}
                        >
                          {savingEdit ? 'Saving...' : 'Save'}
                        </button>
                        {adjusted && (
                          <button
                            onClick={() => clearAdjustment(e)}
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
                  {reviewed.map((e, i) => renderRow(e, i, reviewed))}
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
                      {pending.map((e, i) => renderRow(e, i, pending))}
                    </>
                  )}
                </>
              )
            })()}
          </div>
        )}
      </div>
      <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
    </div>
  )
}
