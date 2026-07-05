import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { supabase } from '../lib/supabase'
import { useAuth } from '../context/AuthContext'
import { getPlan, FREE_TRIP_LIMIT } from '../lib/plan'

const STATUS_COLORS = {
  planning: { background: '#f5e6c8', color: '#8b5e3c' },
  active: { background: '#dcfce7', color: '#15803d' },
  completed: { background: '#f3f4f6', color: '#6b7280' },
}

const card = {
  background: 'white', borderRadius: 16,
  border: '1px solid #e8d5b0', padding: 20, marginBottom: 12,
}

function daysElapsed(startDate) {
  if (!startDate) return 1
  const start = new Date(startDate)
  start.setHours(0, 0, 0, 0)
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  return Math.max(1, Math.round((today - start) / 86400000))
}

function fmt(n) {
  return Number(n).toLocaleString('en-US', { minimumFractionDigits: 0, maximumFractionDigits: 0 })
}

function runwayStopDate(days) {
  const d = new Date()
  d.setDate(d.getDate() + Math.floor(days))
  return d.toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: 'numeric' })
}

export default function Dashboard() {
  const { user } = useAuth()
  const [trips, setTrips] = useState([])
  const [loading, setLoading] = useState(true)
  const [availableFunds, setAvailableFunds] = useState(
    () => localStorage.getItem('campcost_funds') || ''
  )
  const [editingFunds, setEditingFunds] = useState(false)
  const [fundsInput, setFundsInput] = useState('')
  const plan = getPlan()
  const [showLimitMsg, setShowLimitMsg] = useState(false)
  const [confirmDeleteId, setConfirmDeleteId] = useState(null)
  const [deleting, setDeleting] = useState(false)

  useEffect(() => {
    const fetchTrips = async () => {
      const { data, error } = await supabase
        .from('trips')
        .select('*, expenses(amount, adjusted_amount, reviewed)')
        .eq('user_id', user.id)
        .order('created_at', { ascending: false })
      if (!error) setTrips(data ?? [])
      setLoading(false)
    }
    fetchTrips()
  }, [user.id])

  const deleteTrip = async (tripId) => {
    setDeleting(true)
    // Delete child rows first (in case no DB cascade is configured)
    await supabase.from('expenses').delete().eq('trip_id', tripId)
    await supabase.from('budget_categories').delete().eq('trip_id', tripId)
    await supabase.from('trip_stops').delete().eq('trip_id', tripId)
    await supabase.from('trips').delete().eq('id', tripId)
    setTrips(prev => prev.filter(t => t.id !== tripId))
    setConfirmDeleteId(null)
    setDeleting(false)
  }

  const saveFunds = () => {
    const val = parseFloat(fundsInput)
    if (!isNaN(val) && val > 0) {
      localStorage.setItem('campcost_funds', val.toString())
      setAvailableFunds(val.toString())
    }
    setEditingFunds(false)
  }

  const totalSpent = (trip) =>
    (trip.expenses ?? [])
      .filter(e => e.reviewed !== false)
      .reduce((sum, e) => sum + (e.adjusted_amount != null ? Number(e.adjusted_amount) : Number(e.amount)), 0)

  // Find the most relevant active trip to feature at the top
  const today = new Date()
  today.setHours(0, 0, 0, 0)
  const currentTrip = trips.find(t => t.status === 'active') ||
    trips.find(t => t.status === 'planning' && t.start_date && new Date(t.start_date) <= today) ||
    trips[0]

  const currentSpent = currentTrip ? totalSpent(currentTrip) : 0
  const currentBudget = currentTrip ? Number(currentTrip.total_budget) || 0 : 0
  const days = currentTrip ? daysElapsed(currentTrip.start_date) : 1
  const burnRate = currentSpent > 0 ? currentSpent / days : 0
  const budgetPct = currentBudget > 0 ? Math.min((currentSpent / currentBudget) * 100, 100) : 0
  const budgetBarColor = budgetPct >= 90 ? '#d4622a' : budgetPct >= 70 ? '#e07440' : '#2d5a27'

  const funds = parseFloat(availableFunds) || 0
  const runwayDays = burnRate > 0 && funds > 0 ? funds / burnRate : 0

  if (loading) {
    return (
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'center', minHeight: '60vh' }}>
        <div style={{ width: 32, height: 32, border: '4px solid #2d5a27', borderTopColor: 'transparent', borderRadius: '50%', animation: 'spin 0.8s linear infinite' }} />
        <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
      </div>
    )
  }

  return (
    <div style={{ maxWidth: 1024, margin: '0 auto', padding: '32px 16px' }}>

      {/* Header */}
      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 24 }}>
        <div>
          <h1 style={{ fontSize: 24, fontWeight: 700, color: '#4a3728', margin: 0 }}>CampCost</h1>
          <p style={{ fontSize: 14, color: '#8b5e3c', marginTop: 4 }}>What does the road cost you?</p>
        </div>
        {plan === 'free' && trips.length >= FREE_TRIP_LIMIT ? (
          <div style={{ textAlign: 'right' }}>
            <button
              onClick={() => setShowLimitMsg(v => !v)}
              style={{ background: '#c4956a', color: 'white', border: 'none', padding: '10px 18px', borderRadius: 12, fontSize: 14, fontWeight: 600, cursor: 'pointer', whiteSpace: 'nowrap' }}
            >
              🔒 New Trip
            </button>
            {showLimitMsg && (
              <div style={{ marginTop: 8, background: 'white', border: '1px solid #e8d5b0', borderRadius: 12, padding: '12px 16px', textAlign: 'left', minWidth: 240 }}>
                <p style={{ fontSize: 13, fontWeight: 600, color: '#4a3728', margin: '0 0 4px' }}>
                  {trips.length}/{FREE_TRIP_LIMIT} trips used
                </p>
                <p style={{ fontSize: 12, color: '#8b5e3c', margin: '0 0 10px' }}>
                  Upgrade to Pro for unlimited trips, auto-sync, and more.
                </p>
                <Link to="/account" style={{ fontSize: 13, fontWeight: 600, color: '#2d5a27', textDecoration: 'none' }}>
                  Upgrade to Pro →
                </Link>
              </div>
            )}
          </div>
        ) : (
          <Link
            to="/trips/new"
            style={{ background: '#2d5a27', color: 'white', padding: '10px 18px', borderRadius: 12, fontSize: 14, fontWeight: 600, textDecoration: 'none', whiteSpace: 'nowrap' }}
          >
            + New Trip
          </Link>
        )}
      </div>

      {trips.length > 0 && currentTrip && (
        <>
          {/* ── CURRENT TRIP STATUS (FREE) ── */}
          <div style={{ ...card, background: 'linear-gradient(135deg, #2d5a27 0%, #3d7a35 100%)', border: 'none', color: 'white', marginBottom: 12 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: 20 }}>
              <div>
                <p style={{ fontSize: 12, opacity: 0.75, margin: '0 0 4px', textTransform: 'uppercase', letterSpacing: '0.05em' }}>Current Trip</p>
                <h2 style={{ fontSize: 20, fontWeight: 700, margin: 0 }}>{currentTrip.name}</h2>
                {currentTrip.destination && (
                  <p style={{ fontSize: 13, opacity: 0.75, margin: '2px 0 0' }}>{currentTrip.destination}</p>
                )}
              </div>
              <Link to={`/trips/${currentTrip.id}`} style={{
                background: 'rgba(255,255,255,0.15)', color: 'white',
                padding: '7px 14px', borderRadius: 10, fontSize: 13,
                fontWeight: 600, textDecoration: 'none'
              }}>
                View →
              </Link>
            </div>

            {/* Key stats row — FREE */}
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 16, marginBottom: 20 }}>
              <div>
                <p style={{ fontSize: 11, opacity: 0.7, margin: '0 0 4px', textTransform: 'uppercase', letterSpacing: '0.05em' }}>Spent</p>
                <p style={{ fontSize: 22, fontWeight: 700, margin: 0 }}>${fmt(currentSpent)}</p>
                <p style={{ fontSize: 11, opacity: 0.65, margin: '2px 0 0' }}>of ${fmt(currentBudget)} budget</p>
              </div>
              <div>
                <p style={{ fontSize: 11, opacity: 0.7, margin: '0 0 4px', textTransform: 'uppercase', letterSpacing: '0.05em' }}>Burn Rate</p>
                <p style={{ fontSize: 22, fontWeight: 700, margin: 0 }}>${burnRate > 0 ? burnRate.toFixed(0) : '—'}<span style={{ fontSize: 13, fontWeight: 400 }}>/day</span></p>
                <p style={{ fontSize: 11, opacity: 0.65, margin: '2px 0 0' }}>Day {days} of trip</p>
              </div>
              <div>
                <p style={{ fontSize: 11, opacity: 0.7, margin: '0 0 4px', textTransform: 'uppercase', letterSpacing: '0.05em' }}>Budget Left</p>
                <p style={{ fontSize: 22, fontWeight: 700, margin: 0, color: budgetPct >= 90 ? '#fca5a5' : 'white' }}>
                  ${fmt(Math.max(0, currentBudget - currentSpent))}
                </p>
                <p style={{ fontSize: 11, opacity: 0.65, margin: '2px 0 0' }}>{(100 - budgetPct).toFixed(0)}% remaining</p>
              </div>
            </div>

            {/* Budget bar */}
            <div>
              <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11, opacity: 0.75, marginBottom: 6 }}>
                <span>Trip budget</span>
                <span>{budgetPct.toFixed(0)}% used</span>
              </div>
              <div style={{ height: 8, background: 'rgba(255,255,255,0.2)', borderRadius: 99, overflow: 'hidden' }}>
                <div style={{ height: '100%', width: `${budgetPct}%`, background: budgetPct >= 90 ? '#fca5a5' : 'rgba(255,255,255,0.85)', borderRadius: 99, transition: 'width 0.4s' }} />
              </div>
            </div>
          </div>

          {/* ── TRAVEL RUNWAY (PRO) ── */}
          {plan === 'pro' ? (
            <div style={card}>
              <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 16 }}>
                <div>
                  <h3 style={{ fontSize: 16, fontWeight: 700, color: '#4a3728', margin: 0 }}>Travel Runway</h3>
                  <p style={{ fontSize: 13, color: '#8b5e3c', margin: '2px 0 0' }}>How long can you keep going?</p>
                </div>
                <span style={{ fontSize: 11, background: '#dcfce7', color: '#15803d', padding: '3px 10px', borderRadius: 99, fontWeight: 600 }}>Pro</span>
              </div>

              {/* Available funds input */}
              <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 20, background: '#f9f5ee', borderRadius: 12, padding: '12px 16px' }}>
                <div style={{ flex: 1 }}>
                  <p style={{ fontSize: 11, color: '#8b5e3c', margin: '0 0 2px', textTransform: 'uppercase', letterSpacing: '0.05em' }}>Available funds</p>
                  {editingFunds ? (
                    <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                      <span style={{ color: '#4a3728', fontWeight: 600 }}>$</span>
                      <input
                        type="number"
                        value={fundsInput}
                        onChange={e => setFundsInput(e.target.value)}
                        onKeyDown={e => e.key === 'Enter' && saveFunds()}
                        autoFocus
                        placeholder="0"
                        style={{ border: 'none', background: 'transparent', fontSize: 20, fontWeight: 700, color: '#4a3728', outline: 'none', width: 120 }}
                      />
                      <button onClick={saveFunds} style={{ background: '#2d5a27', color: 'white', border: 'none', borderRadius: 8, padding: '4px 12px', fontSize: 13, cursor: 'pointer' }}>Save</button>
                    </div>
                  ) : (
                    <button onClick={() => { setFundsInput(availableFunds); setEditingFunds(true) }} style={{ background: 'none', border: 'none', padding: 0, cursor: 'pointer', textAlign: 'left' }}>
                      <p style={{ fontSize: 20, fontWeight: 700, color: funds > 0 ? '#4a3728' : '#c4956a', margin: 0 }}>
                        {funds > 0 ? `$${fmt(funds)}` : 'Tap to enter →'}
                      </p>
                    </button>
                  )}
                </div>
                {funds > 0 && burnRate > 0 && (
                  <div style={{ textAlign: 'right' }}>
                    <p style={{ fontSize: 11, color: '#8b5e3c', margin: '0 0 2px', textTransform: 'uppercase', letterSpacing: '0.05em' }}>Burn rate</p>
                    <p style={{ fontSize: 16, fontWeight: 700, color: '#4a3728', margin: 0 }}>${burnRate.toFixed(0)}/day</p>
                  </div>
                )}
              </div>

              {funds > 0 && burnRate > 0 ? (
                <>
                  {/* Runway result */}
                  <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginBottom: 16 }}>
                    <div style={{ background: '#f0fdf4', borderRadius: 12, padding: '16px', textAlign: 'center' }}>
                      <p style={{ fontSize: 11, color: '#15803d', margin: '0 0 6px', textTransform: 'uppercase', letterSpacing: '0.05em' }}>You can travel for</p>
                      <p style={{ fontSize: 32, fontWeight: 700, color: '#2d5a27', margin: 0 }}>{Math.floor(runwayDays)}</p>
                      <p style={{ fontSize: 13, color: '#15803d', margin: '2px 0 0' }}>more days</p>
                    </div>
                    <div style={{ background: '#f9f5ee', borderRadius: 12, padding: '16px', textAlign: 'center' }}>
                      <p style={{ fontSize: 11, color: '#8b5e3c', margin: '0 0 6px', textTransform: 'uppercase', letterSpacing: '0.05em' }}>Projected stop</p>
                      <p style={{ fontSize: 15, fontWeight: 700, color: '#4a3728', margin: 0, lineHeight: 1.3 }}>{runwayStopDate(runwayDays)}</p>
                    </div>
                  </div>

                  {/* Stay longer calculator */}
                  {burnRate > 0 && (
                    <div>
                      <p style={{ fontSize: 12, color: '#8b5e3c', fontWeight: 600, marginBottom: 8 }}>What if you stay longer?</p>
                      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 8 }}>
                        {[7, 14, 30].map(d => (
                          <div key={d} style={{ background: '#f9f5ee', borderRadius: 10, padding: '10px 12px', textAlign: 'center' }}>
                            <p style={{ fontSize: 12, color: '#8b5e3c', margin: '0 0 4px' }}>+{d} days</p>
                            <p style={{ fontSize: 15, fontWeight: 700, color: '#d4622a', margin: 0 }}>−${fmt(burnRate * d)}</p>
                          </div>
                        ))}
                      </div>
                    </div>
                  )}
                </>
              ) : (
                <div style={{ textAlign: 'center', padding: '8px 0 4px' }}>
                  <p style={{ fontSize: 13, color: '#8b5e3c' }}>
                    {burnRate === 0 ? 'Add expenses to your trip to calculate runway.' : 'Enter your available funds above to see your runway.'}
                  </p>
                </div>
              )}
            </div>
          ) : (
            /* Locked runway card for free users */
            <div style={{ ...card, position: 'relative', overflow: 'hidden' }}>
              <div style={{ filter: 'blur(3px)', pointerEvents: 'none', userSelect: 'none' }}>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginBottom: 16 }}>
                  <div style={{ background: '#f0fdf4', borderRadius: 12, padding: 16, textAlign: 'center' }}>
                    <p style={{ fontSize: 11, color: '#15803d', margin: '0 0 6px' }}>You can travel for</p>
                    <p style={{ fontSize: 32, fontWeight: 700, color: '#2d5a27', margin: 0 }}>96</p>
                    <p style={{ fontSize: 13, color: '#15803d', margin: '2px 0 0' }}>more days</p>
                  </div>
                  <div style={{ background: '#f9f5ee', borderRadius: 12, padding: 16, textAlign: 'center' }}>
                    <p style={{ fontSize: 11, color: '#8b5e3c', margin: '0 0 6px' }}>Projected stop</p>
                    <p style={{ fontSize: 15, fontWeight: 700, color: '#4a3728', margin: 0 }}>September 2, 2026</p>
                  </div>
                </div>
                <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 8 }}>
                  {[7, 14, 30].map(d => (
                    <div key={d} style={{ background: '#f9f5ee', borderRadius: 10, padding: '10px 12px', textAlign: 'center' }}>
                      <p style={{ fontSize: 12, color: '#8b5e3c', margin: '0 0 4px' }}>+{d} days</p>
                      <p style={{ fontSize: 15, fontWeight: 700, color: '#d4622a', margin: 0 }}>−$xxx</p>
                    </div>
                  ))}
                </div>
              </div>
              {/* Lock overlay */}
              <div style={{
                position: 'absolute', inset: 0,
                display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center',
                background: 'rgba(255,255,255,0.6)', borderRadius: 16,
              }}>
                <span style={{ fontSize: 28, marginBottom: 8 }}>🔒</span>
                <p style={{ fontSize: 15, fontWeight: 700, color: '#4a3728', margin: '0 0 4px' }}>Travel Runway</p>
                <p style={{ fontSize: 13, color: '#8b5e3c', margin: '0 0 14px', textAlign: 'center', maxWidth: 220 }}>
                  See exactly how long your money lasts at your current burn rate.
                </p>
                <Link to="/account" style={{
                  background: '#2d5a27', color: 'white', padding: '9px 20px',
                  borderRadius: 10, fontSize: 13, fontWeight: 600, textDecoration: 'none'
                }}>
                  Upgrade to Pro
                </Link>
              </div>
            </div>
          )}
        </>
      )}

      {/* ── ALL TRIPS ── */}
      <div style={{ marginTop: 28, marginBottom: 12, display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
        <h2 style={{ fontSize: 16, fontWeight: 700, color: '#4a3728', margin: 0 }}>All Trips</h2>
      </div>

      {trips.length === 0 ? (
        <div style={{ textAlign: 'center', padding: '80px 16px' }}>
          <div style={{ fontSize: 56, marginBottom: 16 }}>🏕️</div>
          <h2 style={{ fontSize: 20, fontWeight: 600, color: '#4a3728', marginBottom: 8 }}>No trips yet</h2>
          <p style={{ fontSize: 14, color: '#8b5e3c', marginBottom: 24 }}>Create your first trip to start tracking costs</p>
          <Link to="/trips/new" style={{
            background: '#2d5a27', color: 'white', padding: '12px 24px',
            borderRadius: 12, fontSize: 14, fontWeight: 600,
            textDecoration: 'none', display: 'inline-block'
          }}>
            Plan your first trip
          </Link>
        </div>
      ) : (
        <div style={{ display: 'grid', gap: 12, gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))' }}>
          {trips.map((trip, index) => {
            const spent = totalSpent(trip)
            const budget = Number(trip.total_budget) || 0
            const pct = budget > 0 ? Math.min((spent / budget) * 100, 100) : 0
            const barColor = pct >= 90 ? '#d4622a' : pct >= 70 ? '#e07440' : '#2d5a27'
            const statusStyle = STATUS_COLORS[trip.status] || STATUS_COLORS.planning
            const locked = plan === 'free' && index >= FREE_TRIP_LIMIT

            const cardContent = (
              <>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: 12 }}>
                  <div>
                    <h3 style={{ fontSize: 15, fontWeight: 600, color: locked ? '#b0a090' : '#4a3728', margin: 0 }}>{trip.name}</h3>
                    {trip.destination && (
                      <p style={{ fontSize: 12, color: locked ? '#c4b5a5' : '#8b5e3c', marginTop: 2 }}>{trip.destination}</p>
                    )}
                  </div>
                  {locked ? (
                    <span style={{ fontSize: 11, padding: '3px 8px', borderRadius: 99, fontWeight: 600, background: '#f0ebe4', color: '#b0a090' }}>🔒 Pro</span>
                  ) : (
                    <span style={{ fontSize: 11, padding: '3px 8px', borderRadius: 99, fontWeight: 600, ...statusStyle }}>{trip.status}</span>
                  )}
                </div>
                <div style={{ marginBottom: 8 }}>
                  <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 12, color: locked ? '#c4b5a5' : '#8b5e3c', marginBottom: 6 }}>
                    <span>${fmt(spent)} spent</span>
                    <span>${fmt(budget)} budget</span>
                  </div>
                  <div style={{ height: 8, background: '#f0ebe4', borderRadius: 99, overflow: 'hidden' }}>
                    <div style={{ height: '100%', width: `${pct}%`, background: locked ? '#c4b5a5' : barColor, borderRadius: 99 }} />
                  </div>
                </div>
                {(trip.start_date || trip.end_date) && (
                  <p style={{ fontSize: 12, color: locked ? '#c4b5a5' : '#8b5e3c', marginTop: 10 }}>
                    {trip.start_date}{trip.end_date ? ` → ${trip.end_date}` : ''}
                  </p>
                )}
                {locked && (
                  <Link to="/account" onClick={e => e.stopPropagation()} style={{ fontSize: 12, color: '#8b5e3c', fontWeight: 600, display: 'block', marginTop: 10, textDecoration: 'none' }}>
                    Upgrade to Pro to access →
                  </Link>
                )}
              </>
            )

            const isConfirming = confirmDeleteId === trip.id

            return locked ? (
              <div key={trip.id} style={{ background: '#faf7f3', borderRadius: 16, border: '1px solid #e8d5b0', padding: 16, opacity: 0.7, cursor: 'default' }}>
                {cardContent}
              </div>
            ) : isConfirming ? (
              <div key={trip.id} style={{ background: 'white', borderRadius: 16, border: '1px solid #f5c5b0', padding: 16 }}>
                {cardContent}
                <div style={{ marginTop: 14, background: '#fff5f0', border: '1px solid #f5c5b0', borderRadius: 10, padding: '12px 14px' }}>
                  <p style={{ fontSize: 13, fontWeight: 700, color: '#d4622a', margin: '0 0 4px' }}>Delete "{trip.name}"?</p>
                  <p style={{ fontSize: 12, color: '#8b5e3c', margin: '0 0 12px' }}>This removes the trip and all its expenses permanently.</p>
                  <div style={{ display: 'flex', gap: 8 }}>
                    <button
                      onClick={() => deleteTrip(trip.id)}
                      disabled={deleting}
                      style={{ flex: 1, background: '#d4622a', color: 'white', border: 'none', borderRadius: 8, padding: '8px', fontSize: 13, fontWeight: 600, cursor: 'pointer', opacity: deleting ? 0.6 : 1 }}
                    >
                      {deleting ? 'Deleting...' : 'Delete'}
                    </button>
                    <button
                      onClick={() => setConfirmDeleteId(null)}
                      style={{ flex: 1, background: '#f5e6c8', color: '#8b5e3c', border: 'none', borderRadius: 8, padding: '8px', fontSize: 13, fontWeight: 600, cursor: 'pointer' }}
                    >
                      Cancel
                    </button>
                  </div>
                </div>
              </div>
            ) : (
              <div key={trip.id} style={{ position: 'relative' }}>
                <Link
                  to={`/trips/${trip.id}`}
                  style={{ background: 'white', borderRadius: 16, border: '1px solid #e8d5b0', padding: 16, textDecoration: 'none', display: 'block', transition: 'box-shadow 0.2s' }}
                  onMouseEnter={e => e.currentTarget.style.boxShadow = '0 4px 12px rgba(0,0,0,0.08)'}
                  onMouseLeave={e => e.currentTarget.style.boxShadow = 'none'}
                >
                  {cardContent}
                </Link>
                <button
                  onClick={e => { e.preventDefault(); setConfirmDeleteId(trip.id) }}
                  title="Delete trip"
                  style={{
                    position: 'absolute', top: 10, right: 10,
                    background: 'none', border: 'none', color: '#c4b5a5',
                    fontSize: 18, cursor: 'pointer', lineHeight: 1, padding: '2px 6px',
                    borderRadius: 6,
                  }}
                  onMouseEnter={e => { e.currentTarget.style.background = '#fff0ee'; e.currentTarget.style.color = '#d4622a' }}
                  onMouseLeave={e => { e.currentTarget.style.background = 'none'; e.currentTarget.style.color = '#c4b5a5' }}
                >
                  ×
                </button>
              </div>
            )
          })}
        </div>
      )}

      <style>{`@keyframes spin { to { transform: rotate(360deg); } }`}</style>
    </div>
  )
}
